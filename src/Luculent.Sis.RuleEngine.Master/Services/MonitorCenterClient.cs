using System.Net.Http.Json;
using System.Text.Json;
using Luculent.Sis.RuleEngine.Shared.Models;

namespace Luculent.Sis.RuleEngine.Master.Services;

/// <summary>
/// 与 Monitor Center 的 HTTP 客户端。
/// Rule Engine 启动时从 Monitor Center 拉取全量监视项。
/// Monitor Center 使用 ABP 动态 Web API。
/// </summary>
public class MonitorCenterClient
{
    private readonly HttpClient _http;
    private readonly ILogger<MonitorCenterClient> _logger;

    public MonitorCenterClient(HttpClient http, ILogger<MonitorCenterClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <summary>
    /// 从 Monitor Center 拉取全量监视项。
    /// 调用 ABP 动态 Web API: POST /api/services/app/monitorDataForPublic/GetAllMonitors
    /// </summary>
    public async Task<List<MonitorConfig>> FetchAllMonitorsAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("从 Monitor Center 拉取全量监视项: {Url}", _http.BaseAddress);

        var url = "/api/services/app/monitorDataForPublic/GetAllMonitors";
        var response = await _http.PostAsync(url, null, ct);
        response.EnsureSuccessStatusCode();

        // ABP 响应包装格式: { "result": [...], "success": true }
        using var doc = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: ct);
        var root = doc!.RootElement;

        var items = root.TryGetProperty("result", out var resultProp)
            ? resultProp
            : root;

        var monitors = JsonSerializer.Deserialize<List<MonitorConfig>>(items.GetRawText());
        _logger.LogInformation("从 Monitor Center 拉取完成: {Count} 个监视项", monitors?.Count ?? 0);

        return monitors ?? new List<MonitorConfig>();
    }
}
