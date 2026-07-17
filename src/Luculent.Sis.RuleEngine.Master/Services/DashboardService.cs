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

        return new DashboardData
        {
            Timestamp = DateTime.UtcNow,
            TotalMonitors = _config.Count,
            ActiveAlarmCount = activeAlarms.Count,
            ActiveWorkers = _workers.ActiveCount,
            AlarmsByStatus = activeAlarms
                .GroupBy(a => a.StatusKey)
                .ToDictionary(g => g.Key, g => g.Count()),
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
    public Dictionary<string, int> AlarmsByStatus { get; set; } = new();
    public List<AlarmSnapshotDTO> TopAlarmMonitors { get; set; } = new();
}
