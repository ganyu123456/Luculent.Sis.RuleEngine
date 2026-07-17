using Luculent.Sis.RuleEngine.Shared.DTOs;
using Luculent.Sis.RuleEngine.Shared.Interfaces;

namespace Luculent.Sis.RuleEngine.Master.Services;

/// <summary>
/// 报警查询服务。生产环境查询 Redis + ClickHouse。
/// </summary>
public class AlarmQueryService
{
    private readonly IAlarmWriter _alarmWriter;
    private readonly ILogger<AlarmQueryService> _logger;

    public AlarmQueryService(IAlarmWriter alarmWriter, ILogger<AlarmQueryService> logger)
    {
        _alarmWriter = alarmWriter;
        _logger = logger;
    }

    public async Task<RealtimeAlarmResponse> GetRealtimeAlarmsAsync()
    {
        var alarms = await _alarmWriter.GetActiveAlarmsAsync();
        return new RealtimeAlarmResponse
        {
            Items = alarms.Select(a => new AlarmSnapshotDTO
            {
                MonitorId = a.MonitorId,
                MonitorKey = a.MonitorKey,
                MonitorName = a.MonitorName,
                StatusKey = a.StatusKey,
                StatusName = a.StatusName,
                Value = a.Value,
                OccurTime = a.OccurTime,
                WorkerId = a.WorkerId,
            }).ToList(),
        };
    }

    public async Task<AlarmSnapshotDTO?> GetRealtimeAlarmAsync(string monitorId)
    {
        var alarm = await _alarmWriter.GetAlarmAsync(monitorId);
        if (alarm == null) return null;

        return new AlarmSnapshotDTO
        {
            MonitorId = alarm.MonitorId,
            MonitorKey = alarm.MonitorKey,
            MonitorName = alarm.MonitorName,
            StatusKey = alarm.StatusKey,
            StatusName = alarm.StatusName,
            Value = alarm.Value,
            OccurTime = alarm.OccurTime,
            WorkerId = alarm.WorkerId,
        };
    }
}
