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

    private const int BatchSize = 20000;
    private const int MaxIterations = 100;

    public MonitorCenterClient(HttpClient http, ILogger<MonitorCenterClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <summary>
    /// 从 Monitor Center 分批拉取全量监视项。
    /// 每批 {BatchSize} 条，循环拉取直到无更多数据。
    /// </summary>
    public async Task<List<MonitorConfig>> FetchAllMonitorsAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("从 Monitor Center 分批拉取监视项: {Url}", _http.BaseAddress);

        var allMonitors = new List<MonitorConfig>();
        var skip = 0;
        var iteration = 0;

        while (!ct.IsCancellationRequested)
        {
            if (++iteration > MaxIterations)
            {
                _logger.LogError("MonitorCenter 分页达到最大迭代次数 {Max}, 中止", MaxIterations);
                break;
            }
            var url = $"/api/services/monitorcenter/monitorDataForPublic/GetAllMonitors?skip={skip}&take={BatchSize}";
            var response = await _http.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();

            using var doc = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: ct);
            var root = doc!.RootElement;

            var items = root.TryGetProperty("result", out var resultProp)
                ? resultProp
                : root;

            var batch = JsonSerializer.Deserialize<List<MonitorConfig>>(items.GetRawText(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? new List<MonitorConfig>();

            if (batch.Count == 0)
                break;

            allMonitors.AddRange(batch);
            _logger.LogDebug("MonitorCenter 批次: skip={Skip}, count={Count}", skip, batch.Count);
            skip += batch.Count;

            if (batch.Count < BatchSize)
                break;
        }

        _logger.LogInformation("从 Monitor Center 拉取完成: {Count} 个监视项", allMonitors.Count);
        return allMonitors;
    }

    /// <summary>
    /// 从 Monitor Center 分批拉取全量前置规则定义。
    /// </summary>
    public async Task<List<PreruleDefinition>> FetchAllPrerulesAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("从 Monitor Center 分批拉取前置规则: {Url}", _http.BaseAddress);

        var allPrerules = new List<PreruleDefinition>();
        var skip = 0;
        var iteration = 0;

        while (!ct.IsCancellationRequested)
        {
            if (++iteration > MaxIterations)
            {
                _logger.LogError("MonitorCenter 前置规则分页达到最大迭代次数 {Max}, 中止", MaxIterations);
                break;
            }
            var url = $"/api/services/monitorcenter/monitorDataForPublic/GetAllPrerules?skip={skip}&take={BatchSize}";
            var response = await _http.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();

            using var doc = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: ct);
            var root = doc!.RootElement;

            var items = root.TryGetProperty("result", out var resultProp)
                ? resultProp
                : root;

            var batch = JsonSerializer.Deserialize<List<PreruleDefinition>>(items.GetRawText(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? new List<PreruleDefinition>();

            if (batch.Count == 0)
                break;

            allPrerules.AddRange(batch);
            _logger.LogDebug("MonitorCenter 前置规则批次: skip={Skip}, count={Count}", skip, batch.Count);
            skip += batch.Count;

            if (batch.Count < BatchSize)
                break;
        }

        _logger.LogInformation("从 Monitor Center 拉取前置规则完成: {Count} 条", allPrerules.Count);
        return allPrerules;
    }
}
