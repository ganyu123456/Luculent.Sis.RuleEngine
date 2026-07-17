using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Luculent.Sis.RuleEngine.Shared.DTOs;
using Luculent.Sis.RuleEngine.Shared.Models;
using Microsoft.Extensions.Logging;

namespace Luculent.Sis.RuleEngine.Worker.DataSource;

/// <summary>
/// 从 Master API 拉取监视项配置的 HTTP 客户端。
/// 用于多 Worker 部署场景，Worker 启动时从 Master 获取全量配置。
/// </summary>
public class MasterConfigClient
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly ILogger<MasterConfigClient> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public MasterConfigClient(HttpClient http, IConfiguration configuration, ILogger<MasterConfigClient> logger)
    {
        _http = http;
        _baseUrl = configuration.GetValue<string>("MASTER_API_URL") ?? "http://master:11081";
        _logger = logger;
    }

    /// <summary>
    /// 从 Master 拉取分配给本 Worker 的监视项配置。
    /// </summary>
    public async Task<List<MonitorConfig>> FetchFullConfigAsync(string workerId, CancellationToken ct = default)
    {
        try
        {
            var url = $"{_baseUrl}/api/ruleengine/sync/full/config?workerId={Uri.EscapeDataString(workerId)}";
            var monitors = await _http.GetFromJsonAsync<List<MonitorConfig>>(url, ct);
            return monitors ?? new List<MonitorConfig>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "无法从 Master 拉取配置: {Url}", _baseUrl);
            return new List<MonitorConfig>();
        }
    }

    /// <summary>
    /// 向 Master 注册 Worker。
    /// </summary>
    public async Task RegisterAsync(string workerId, CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(new
        {
            workerId,
            grpcAddress = $"http://{workerId}:50051",
            capacity = 100000,
        }, JsonOpts);

        var content = new StringContent(payload, Encoding.UTF8, "application/json");
        var resp = await _http.PostAsync($"{_baseUrl}/api/ruleengine/workers/register", content, ct);
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// 向 Master 发送心跳，同时上报监视项数量。
    /// </summary>
    public async Task HeartbeatAsync(string workerId, int monitorCount, CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/api/ruleengine/workers/{Uri.EscapeDataString(workerId)}/heartbeat?monitorCount={monitorCount}";
        var resp = await _http.PostAsync(url, null, ct);
        resp.EnsureSuccessStatusCode();
    }
}
