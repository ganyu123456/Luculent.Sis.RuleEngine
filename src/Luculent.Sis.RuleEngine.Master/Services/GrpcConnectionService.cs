using System.Collections.Concurrent;
using System.Text.Json;
using Grpc.Core;
using Luculent.Sis.RuleEngine.Shared.Grpc;
using Luculent.Sis.RuleEngine.Shared.Models;
using Luculent.Sis.RuleEngine.Worker.Storage;

namespace Luculent.Sis.RuleEngine.Master.Services;

/// <summary>
/// Master 侧 gRPC 服务。每个 Worker 建立一个双向流连接，
/// Master 通过此流推送配置变更，Worker 通过此流上报心跳。
/// 连接断开 → Worker 视为离线 → 触发重分区。
/// </summary>
public class GrpcConnectionService : RuleEngineService.RuleEngineServiceBase
{
    private readonly WorkerManager _workerManager;
    private readonly ConfigurationService _configService;
    private readonly PartitionService _partitionService;
    private readonly PreruleDefinitionStore _preruleStore;
    private readonly ILogger<GrpcConnectionService> _logger;

    /// <summary>活跃的 Worker 连接 (workerId → stream writer)</summary>
    private readonly ConcurrentDictionary<string, IServerStreamWriter<MasterMessage>> _connections = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public GrpcConnectionService(
        WorkerManager workerManager,
        ConfigurationService configService,
        PartitionService partitionService,
        PreruleDefinitionStore preruleStore,
        ILogger<GrpcConnectionService> logger)
    {
        _workerManager = workerManager;
        _configService = configService;
        _partitionService = partitionService;
        _preruleStore = preruleStore;
        _logger = logger;
    }

    public override async Task Connect(
        IAsyncStreamReader<WorkerMessage> requestStream,
        IServerStreamWriter<MasterMessage> responseStream,
        ServerCallContext context)
    {
        var workerId = "unknown";
        var cancelled = context.CancellationToken;

        try
        {
            // ① 等待 Register 消息
            if (!await requestStream.MoveNext(cancelled))
            {
                _logger.LogWarning("Worker 连接立即关闭，未发送注册消息");
                return;
            }

            var firstMsg = requestStream.Current;
            if (firstMsg.PayloadCase != WorkerMessage.PayloadOneofCase.Register)
            {
                _logger.LogWarning("Worker 首条消息非注册: {Case}", firstMsg.PayloadCase);
                return;
            }

            workerId = firstMsg.Register.WorkerId;
            _logger.LogInformation("Worker gRPC 连接建立: {WorkerId}", workerId);

            // 注册 Worker
            var worker = new WorkerInfo { WorkerId = workerId };
            await _workerManager.RegisterAsync(worker);

            // 存储连接
            _connections[workerId] = responseStream;

            // ② 推送当前分配给该 Worker 的配置
            await PushConfigAsync(workerId, responseStream, cancelled);

            // 首次注册后触发分区
            await RebalanceIfNeeded();

            // ③ 持续读取心跳，同时监听配置变更
            var heartbeatTask = ReadHeartbeatsAsync(workerId, requestStream, cancelled);
            var configWatchTask = ConfigWatchLoopAsync(workerId, responseStream, cancelled);

            await Task.WhenAny(heartbeatTask, configWatchTask);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Worker {WorkerId} gRPC 流正常关闭", workerId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Worker {WorkerId} gRPC 连接异常", workerId);
        }
        finally
        {
            _connections.TryRemove(workerId, out _);
            await _workerManager.DeregisterAsync(workerId);
            _logger.LogWarning("Worker {WorkerId} 连接断开，已注销", workerId);

            // Worker 断连后触发重分区
            if (_configService.Count > 0)
                await RebalanceIfNeeded();
        }
    }

    private async Task ReadHeartbeatsAsync(
        string workerId,
        IAsyncStreamReader<WorkerMessage> requestStream,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!await requestStream.MoveNext(ct))
                    break;

                if (requestStream.Current.PayloadCase == WorkerMessage.PayloadOneofCase.Heartbeat)
                {
                    var hb = requestStream.Current.Heartbeat;
                    await _workerManager.HeartbeatAsync(workerId, hb.MonitorCount);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Worker {WorkerId} 心跳读取异常", workerId);
                break;
            }
        }
    }

    /// <summary>
    /// 监听配置变更，有新分配时推送给 Worker。
    /// </summary>
    private async Task ConfigWatchLoopAsync(
        string workerId,
        IServerStreamWriter<MasterMessage> responseStream,
        CancellationToken ct)
    {
        var lastPushedVersion = DateTime.MinValue;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), ct);

                var monitors = _configService.GetByWorkerId(workerId);
                if (monitors.Count > 0)
                {
                    // 检查是否有变更 (简化: 用 LastModificationTime 判断)
                    var maxModTime = monitors.Max(m => m.LastModificationTime);
                    if (maxModTime > lastPushedVersion)
                    {
                        await PushConfigAsync(workerId, responseStream, ct);
                        lastPushedVersion = maxModTime;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private const int ChunkSize = 15_000;

    private async Task PushConfigAsync(
        string workerId,
        IServerStreamWriter<MasterMessage> responseStream,
        CancellationToken ct)
    {
        var monitors = _configService.GetByWorkerId(workerId);

        var prerules = _preruleStore.GetAll();
        var prerulesJson = prerules.Count > 0
            ? JsonSerializer.Serialize(prerules, JsonOpts)
            : "";

        var totalChunks = monitors.Count <= ChunkSize
            ? 0
            : (int)Math.Ceiling((double)monitors.Count / ChunkSize);
        var iterations = Math.Max(1, totalChunks);

        for (var i = 0; i < iterations; i++)
        {
            var chunk = totalChunks <= 1
                ? monitors
                : monitors.Skip(i * ChunkSize).Take(ChunkSize).ToList();

            var monitorsJson = JsonSerializer.Serialize(chunk, JsonOpts);

            await responseStream.WriteAsync(new MasterMessage
            {
                ConfigPush = new ConfigPush
                {
                    MonitorsJson = monitorsJson,
                    PrerulesJson = i == 0 ? prerulesJson : "",
                    ChunkIndex = i,
                    TotalChunks = totalChunks,
                }
            }, ct);
        }

        _logger.LogInformation(
            "推送配置到 Worker {WorkerId}: {Count} 监视项, {PreruleCount} 前置规则, {Chunks} 分块",
            workerId, monitors.Count, prerules.Count, iterations);
    }

    private async Task RebalanceIfNeeded()
    {
        var activeWorkers = _workerManager.GetActiveWorkers();
        if (activeWorkers.Count > 0 && _configService.Count > 0)
        {
            var allMonitors = _configService.All.Values.ToList();
            var result = _partitionService.Partition(allMonitors, activeWorkers);
            _configService.SetWorkerAssignments(result.WorkerAssignments);

            foreach (var (wid, monitors) in result.WorkerAssignments)
                await _workerManager.HeartbeatAsync(wid, monitors.Count);

            _logger.LogInformation("gRPC 连接变更后重分区: {WorkerCount} Worker, {MonitorCount} 监视项",
                activeWorkers.Count, allMonitors.Count);

            // 推送配置到各 Worker
            foreach (var (wid, _) in result.WorkerAssignments)
            {
                if (_connections.TryGetValue(wid, out var stream))
                {
                    try
                    {
                        await PushConfigAsync(wid, stream, CancellationToken.None);
                    }
                    catch { }
                }
            }
        }
    }

    public IEnumerable<string> GetConnectedWorkerIds() => _connections.Keys;

    /// <summary>
    /// 向指定 Worker 推送当前分配（供外部调用，如启动分区后）。
    /// </summary>
    public async Task PushToWorkersAsync(Dictionary<string, List<MonitorConfig>> assignments)
    {
        foreach (var (wid, _) in assignments)
        {
            if (_connections.TryGetValue(wid, out var stream))
            {
                try
                {
                    await PushConfigAsync(wid, stream, CancellationToken.None);
                }
                catch { }
            }
        }
    }
}
