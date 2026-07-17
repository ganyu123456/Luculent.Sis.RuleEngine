using System.Collections.Concurrent;
using Luculent.Sis.RuleEngine.Shared.Models;

namespace Luculent.Sis.RuleEngine.Master.Services;

/// <summary>
/// 全量配置管理器。持有所有监视项配置（~2GB / 100万项）。
/// </summary>
public class ConfigurationService
{
    private readonly ConcurrentDictionary<string, MonitorConfig> _configs = new();
    private readonly ILogger<ConfigurationService> _logger;

    public int Count => _configs.Count;
    public IReadOnlyDictionary<string, MonitorConfig> All => _configs;

    public ConfigurationService(ILogger<ConfigurationService> logger)
    {
        _logger = logger;
    }

    public void LoadFull(IEnumerable<MonitorConfig> monitors)
    {
        _configs.Clear();
        foreach (var m in monitors)
            _configs[m.Id] = m;
        _logger.LogInformation("全量配置加载完成: {Count} 条", _configs.Count);
    }

    public void Add(IEnumerable<MonitorConfig> monitors)
    {
        foreach (var m in monitors)
            _configs[m.Id] = m;
        _logger.LogInformation("新增配置: {Count} 条", monitors.Count());
    }

    public void Update(IEnumerable<MonitorConfig> monitors)
    {
        foreach (var m in monitors)
            _configs[m.Id] = m;
        _logger.LogInformation("更新配置: {Count} 条", monitors.Count());
    }

    public void Remove(IEnumerable<string> monitorIds)
    {
        foreach (var id in monitorIds)
            _configs.TryRemove(id, out _);
        _logger.LogInformation("删除配置: {Count} 条", monitorIds.Count());
    }

    public MonitorConfig? Get(string monitorId)
    {
        _configs.TryGetValue(monitorId, out var config);
        return config;
    }

    public List<MonitorConfig> GetByWorkerId(string workerId)
    {
        // 需要结合分区映射表查询
        return _configs.Values.ToList();
    }
}
