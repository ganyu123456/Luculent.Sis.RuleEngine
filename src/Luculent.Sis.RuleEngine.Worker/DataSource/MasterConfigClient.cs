using System.Net.Http.Json;
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

    public MasterConfigClient(HttpClient http, IConfiguration configuration, ILogger<MasterConfigClient> logger)
    {
        _http = http;
        _baseUrl = configuration.GetValue<string>("MASTER_API_URL") ?? "http://master:11081";
        _logger = logger;
    }

    /// <summary>
    /// 从 Master 拉取全量监视项配置。
    /// </summary>
    public async Task<List<MonitorConfig>> FetchFullConfigAsync(CancellationToken ct = default)
    {
        try
        {
            var url = $"{_baseUrl}/api/ruleengine/sync/full/config";
            var monitors = await _http.GetFromJsonAsync<List<MonitorConfig>>(url, ct);
            return monitors ?? new List<MonitorConfig>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "无法从 Master 拉取配置: {Url}", _baseUrl);
            return new List<MonitorConfig>();
        }
    }
}
