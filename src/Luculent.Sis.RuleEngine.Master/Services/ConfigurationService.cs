using System.Collections.Concurrent;
using Luculent.Sis.RuleEngine.Shared.Models;

namespace Luculent.Sis.RuleEngine.Master.Services;

/// <summary>
/// 全量配置管理器。持有所有监视项配置及分区分配结果。
/// </summary>
public class ConfigurationService
{
    private readonly ConcurrentDictionary<string, MonitorConfig> _configs = new();
    private readonly ConcurrentDictionary<string, List<MonitorConfig>> _workerAssignments = new();
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

    /// <summary>
    /// 存储分区结果：每个 Worker 分配到哪些监视项。
    /// </summary>
    public void SetWorkerAssignments(Dictionary<string, List<MonitorConfig>> assignments)
    {
        _workerAssignments.Clear();
        foreach (var (workerId, monitors) in assignments)
            _workerAssignments[workerId] = monitors;
        _logger.LogInformation("分区分配已更新: {WorkerCount} Worker", assignments.Count);
    }

    /// <summary>
    /// 获取指定 Worker 分配的监视项。
    /// 如果没有分区结果（无注册 Worker），返回空列表。
    /// </summary>
    public List<MonitorConfig> GetByWorkerId(string workerId)
    {
        if (_workerAssignments.TryGetValue(workerId, out var monitors))
            return monitors;
        return new List<MonitorConfig>();
    }

    /// <summary>
    /// 获取所有 Worker 的分配数量。
    /// </summary>
    public IReadOnlyDictionary<string, int> GetWorkerDistribution()
    {
        return _workerAssignments.ToDictionary(kv => kv.Key, kv => kv.Value.Count);
    }
}
