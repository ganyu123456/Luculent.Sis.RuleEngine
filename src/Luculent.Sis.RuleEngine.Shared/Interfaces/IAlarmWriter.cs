using Luculent.Sis.RuleEngine.Shared.Models;

namespace Luculent.Sis.RuleEngine.Shared.Interfaces;

public interface IAlarmWriter
{
    Task WriteRealtimeAlarmAsync(AlarmSnapshot alarm);
    Task ClearRealtimeAlarmAsync(string monitorId);
    Task WriteHistoryAlarmAsync(AlarmEvent alarmEvent);
    Task<IReadOnlyList<AlarmSnapshot>> GetActiveAlarmsAsync();
    Task<AlarmSnapshot?> GetAlarmAsync(string monitorId);
}
