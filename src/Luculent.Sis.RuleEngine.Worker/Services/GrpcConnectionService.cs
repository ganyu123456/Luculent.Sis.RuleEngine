using System.Text.Json;
using Grpc.Core;
using Grpc.Net.Client;
using Luculent.Sis.RuleEngine.Shared.Grpc;
using Luculent.Sis.RuleEngine.Shared.Models;
using Luculent.Sis.RuleEngine.Worker.Storage;
using Microsoft.Extensions.Logging;

namespace Luculent.Sis.RuleEngine.Worker.Services;

/// <summary>
/// Worker 侧 gRPC 连接服务。维护与 Master 的双向流连接，
/// 发送心跳、接收配置推送、自动重连。
/// </summary>
public class GrpcConnectionService : IAsyncDisposable
{
    private readonly string _workerId;
    private readonly string _masterUrl;
    private readonly WorkerCalculationService _calcService;
    private readonly PreruleDefinitionStore _preruleStore;
    private readonly ILogger<GrpcConnectionService> _logger;

    private GrpcChannel? _channel;
    private AsyncDuplexStreamingCall<WorkerMessage, MasterMessage>? _call;
    private CancellationTokenSource? _cts;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public GrpcConnectionService(
        IConfiguration configuration,
        WorkerCalculationService calcService,
        PreruleDefinitionStore preruleStore,
        ILogger<GrpcConnectionService> logger)
    {
        _workerId = configuration.GetValue<string>("WORKER_ID") ?? Environment.MachineName;
        _masterUrl = configuration.GetValue<string>("MASTER_GRPC_URL")
            ?? configuration.GetValue<string>("MASTER_API_URL")
            ?? "http://master:11083";
        _calcService = calcService;
        _preruleStore = preruleStore;
        _logger = logger;
    }

    /// <summary>
    /// 启动 gRPC 连接并保持。失败时自动重连。
    /// </summary>
    public async Task RunAsync(CancellationToken appCt)
    {
        while (!appCt.IsCancellationRequested)
        {
            try
            {
                _logger.LogInformation("Worker {WorkerId} 开始连接 Master gRPC: {Url}", _workerId, _masterUrl);
                await ConnectAndServeAsync(appCt);
            }
            catch (OperationCanceledException) when (appCt.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Master 重启/部署导致 HTTP/2 连接断开属于正常运维操作，降级为 Info
                var isHttp2Reset = ex.Message.Contains("HTTP/2") || ex.InnerException?.Message.Contains("HTTP/2") == true;
                if (isHttp2Reset)
                    _logger.LogInformation("Worker {WorkerId} gRPC 连接断开 (HTTP/2 reset, 通常是 Master 重启), 5s 后重连", _workerId);
                else
                    _logger.LogWarning(ex, "Worker {WorkerId} gRPC 连接断开，5s 后重连", _workerId);
            }

            if (!appCt.IsCancellationRequested)
                await Task.Delay(TimeSpan.FromSeconds(5), appCt);
        }
    }

    private async Task ConnectAndServeAsync(CancellationToken appCt)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(appCt);
        _channel = GrpcChannel.ForAddress(_masterUrl, new GrpcChannelOptions
        {
            MaxReceiveMessageSize = 50 * 1024 * 1024, // 50MB — 支持 10K+ 监视项配置
            MaxSendMessageSize = 50 * 1024 * 1024,
        });
        var client = new RuleEngineService.RuleEngineServiceClient(_channel);
        _call = client.Connect(cancellationToken: _cts.Token);

        // ① 发送注册消息
        await _call.RequestStream.WriteAsync(new WorkerMessage
        {
            Register = new RegisterRequest { WorkerId = _workerId }
        }, _cts.Token);

        _logger.LogInformation("Worker {WorkerId} gRPC 注册成功", _workerId);

        // ② 启动心跳和配置接收
        var heartbeatTask = HeartbeatLoopAsync(_call.RequestStream, _cts.Token);
        var receiveTask = ReceiveLoopAsync(_call.ResponseStream, _cts.Token);

        await Task.WhenAny(heartbeatTask, receiveTask);

        // 一方结束则关闭整个流
        _cts.Cancel();
    }

    private async Task HeartbeatLoopAsync(
        IClientStreamWriter<WorkerMessage> requestStream,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), ct);

                await requestStream.WriteAsync(new WorkerMessage
                {
                    Heartbeat = new Heartbeat
                    {
                        MonitorCount = _calcService.AssignedMonitors.Count,
                    }
                }, ct);

                _logger.LogDebug("Worker {WorkerId} gRPC 心跳: {Count} 监视项",
                    _workerId, _calcService.AssignedMonitors.Count);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Worker {WorkerId} 心跳发送失败", _workerId);
                break;
            }
        }
    }

    private async Task ReceiveLoopAsync(
        IAsyncStreamReader<MasterMessage> responseStream,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!await responseStream.MoveNext(ct))
                    break;

                var msg = responseStream.Current;
                switch (msg.PayloadCase)
                {
                    case MasterMessage.PayloadOneofCase.ConfigPush:
                        await HandleConfigPush(msg.ConfigPush);
                        break;

                    case MasterMessage.PayloadOneofCase.Shutdown:
                        _logger.LogInformation("Worker {WorkerId} 收到 shutdown: {Reason}",
                            _workerId, msg.Shutdown.Reason);
                        return;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                var isHttp2Reset = ex.Message.Contains("HTTP/2") || ex.InnerException?.Message.Contains("HTTP/2") == true;
                if (isHttp2Reset)
                    _logger.LogInformation("Worker {WorkerId} gRPC 接收异常 (HTTP/2 reset), 将重连", _workerId);
                else
                    _logger.LogWarning(ex, "Worker {WorkerId} gRPC 接收异常", _workerId);
                break;
            }
        }
    }

    private Task HandleConfigPush(ConfigPush push)
    {
        try
        {
            // 处理前置规则定义
            if (!string.IsNullOrEmpty(push.PrerulesJson))
            {
                var prerules = JsonSerializer.Deserialize<List<PreruleDefinition>>(
                    push.PrerulesJson, JsonOpts);
                if (prerules != null && prerules.Count > 0)
                {
                    _preruleStore.LoadAll(prerules);
                    _logger.LogInformation(
                        "Worker {WorkerId} 前置规则更新: {Count} 条",
                        _workerId, prerules.Count);
                }
            }

            // 处理监视项配置
            var monitors = JsonSerializer.Deserialize<List<MonitorConfig>>(
                push.MonitorsJson, JsonOpts);

            if (monitors == null || monitors.Count == 0)
                return Task.CompletedTask;

            var currentIds = new HashSet<string>(_calcService.AssignedMonitors.Keys);
            var newIds = new HashSet<string>(monitors.Select(m => m.Id));
            var added = monitors.Where(m => !currentIds.Contains(m.Id)).ToList();
            var removed = currentIds.Where(id => !newIds.Contains(id)).ToList();

            foreach (var m in monitors)
                _calcService.AssignedMonitors[m.Id] = m;
            foreach (var id in removed)
                _calcService.AssignedMonitors.TryRemove(id, out _);

            _calcService.InvalidateTagNameCache();

            _logger.LogInformation(
                "Worker {WorkerId} 配置更新: {Count} 监视项 (+{Added} -{Removed})",
                _workerId, monitors.Count, added.Count, removed.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Worker {WorkerId} 配置解析失败", _workerId);
        }

        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _call?.Dispose();
        _channel?.Dispose();
        _cts?.Dispose();
    }
}
