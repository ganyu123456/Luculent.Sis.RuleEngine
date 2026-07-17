using Luculent.Sis.RuleEngine.Shared.DTOs;
using Luculent.Sis.RuleEngine.Shared.Interfaces;

namespace Luculent.Sis.RuleEngine.Master.Services;

/// <summary>
/// Dashboard 数据聚合服务：汇总监控项、报警、Worker 状态。
/// </summary>
public class DashboardService
{
    private readonly ConfigurationService _config;
    private readonly IAlarmWriter _alarmWriter;
    private readonly WorkerManager _workers;

    public DashboardService(
        ConfigurationService config,
        IAlarmWriter alarmWriter,
        WorkerManager workers)
    {
        _config = config;
        _alarmWriter = alarmWriter;
        _workers = workers;
    }

    public async Task<DashboardData> GetDashboardDataAsync()
    {
        var activeAlarms = await _alarmWriter.GetActiveAlarmsAsync();

        // 从活跃报警中按 WorkerId 聚合
        var alarmByWorker = activeAlarms.GroupBy(a => a.WorkerId)
            .ToDictionary(g => g.Key, g => g.Count());

        // 分区分配中记录的每个 Worker 的监视项数量
        var distribution = _config.GetWorkerDistribution();

        // 注册过的 Worker
        var registeredWorkers = _workers.GetActiveWorkers().ToDictionary(w => w.WorkerId);

        var workerSet = new HashSet<string>(alarmByWorker.Keys);
        foreach (var id in distribution.Keys) workerSet.Add(id);
        foreach (var id in registeredWorkers.Keys) workerSet.Add(id);

        var workers = workerSet.Select(id =>
        {
            alarmByWorker.TryGetValue(id, out var alarmCount);
            distribution.TryGetValue(id, out var monitorCount);
            registeredWorkers.TryGetValue(id, out var info);
            return new WorkerDashboardInfo
            {
                WorkerId = id,
                MonitorCount = monitorCount,
                AlarmCount = alarmCount,
                Status = info?.Status.ToString() ?? "Online",
                LastHeartbeat = info?.LastHeartbeat ?? DateTime.UtcNow,
            };
        }).OrderByDescending(w => w.AlarmCount).ToList();

        return new DashboardData
        {
            Timestamp = DateTime.UtcNow,
            TotalMonitors = _config.Count,
            ActiveAlarmCount = activeAlarms.Count,
            ActiveWorkers = workers.Count(w => w.Status == "Online"),
            Workers = workers,
            TopAlarmMonitors = activeAlarms
                .Take(20)
                .Select(a => new AlarmSnapshotDTO
                {
                    MonitorId = a.MonitorId,
                    MonitorKey = a.MonitorKey,
                    MonitorName = a.MonitorName,
                    StatusKey = a.StatusKey,
                    StatusName = a.StatusName,
                    Value = a.Value,
                    OccurTime = a.OccurTime,
                    WorkerId = a.WorkerId,
                })
                .ToList(),
        };
    }
}

public class DashboardData
{
    public DateTime Timestamp { get; set; }
    public int TotalMonitors { get; set; }
    public int ActiveAlarmCount { get; set; }
    public int ActiveWorkers { get; set; }
    public List<WorkerDashboardInfo> Workers { get; set; } = new();
    public List<AlarmSnapshotDTO> TopAlarmMonitors { get; set; } = new();
}

public class WorkerDashboardInfo
{
    public string WorkerId { get; set; } = string.Empty;
    public int MonitorCount { get; set; }
    public int AlarmCount { get; set; }
    public string Status { get; set; } = "Online";
    public DateTime LastHeartbeat { get; set; }
}
