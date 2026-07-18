using Luculent.Sis.RuleEngine.Shared.Models;

namespace Luculent.Sis.RuleEngine.Shared.Interfaces;

public interface IAlarmWriter
{
    Task WriteRealtimeAlarmAsync(AlarmSnapshot alarm);
    Task ClearRealtimeAlarmAsync(string monitorId);
    Task WriteHistoryAlarmAsync(AlarmEvent alarmEvent);
    Task<IReadOnlyList<AlarmSnapshot>> GetActiveAlarmsAsync();
    Task<AlarmSnapshot?> GetAlarmAsync(string monitorId);

    /// <summary>
    /// Worker 启动恢复：批量查询各 monitor 的最后一条事件状态键。
    /// 返回 monitorId → statusKey 映射，无事件的 monitor 不出现在字典中。
    /// </summary>
    Task<Dictionary<string, string?>> GetLastEventStatusesAsync(IEnumerable<string> monitorIds);
}
