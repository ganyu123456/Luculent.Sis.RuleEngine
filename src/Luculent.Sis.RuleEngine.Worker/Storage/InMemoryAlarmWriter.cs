using System.Collections.Concurrent;
using System.Text.Json;
using Luculent.Sis.RuleEngine.Shared.Interfaces;
using Luculent.Sis.RuleEngine.Shared.Models;
using Microsoft.Extensions.Logging;

namespace Luculent.Sis.RuleEngine.Worker.Storage;

/// <summary>
/// 基于内存 ConcurrentDictionary 的报警写入实现。
/// 用于开发/测试环境。生产环境应使用 Redis + ClickHouse。
/// </summary>
public class InMemoryAlarmWriter : IAlarmWriter
{
    private readonly ConcurrentDictionary<string, AlarmSnapshot> _activeAlarms = new();
    private readonly ConcurrentBag<AlarmEvent> _history = new();
    private readonly ILogger<InMemoryAlarmWriter> _logger;

    public InMemoryAlarmWriter(ILogger<InMemoryAlarmWriter> logger)
    {
        _logger = logger;
    }

    public Task WriteRealtimeAlarmAsync(AlarmSnapshot alarm)
    {
        _activeAlarms[alarm.MonitorId] = alarm;
        _logger.LogDebug("写入实时报警: {MonitorId} {StatusKey}", alarm.MonitorId, alarm.StatusKey);
        return Task.CompletedTask;
    }

    public Task ClearRealtimeAlarmAsync(string monitorId)
    {
        _activeAlarms.TryRemove(monitorId, out _);
        _logger.LogDebug("清除实时报警: {MonitorId}", monitorId);
        return Task.CompletedTask;
    }

    public Task WriteHistoryAlarmAsync(AlarmEvent alarmEvent)
    {
        _history.Add(alarmEvent);
        _logger.LogDebug("写入历史报警: {MonitorId} {StatusKey}", alarmEvent.MonitorId, alarmEvent.StatusKey);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AlarmSnapshot>> GetActiveAlarmsAsync()
    {
        return Task.FromResult<IReadOnlyList<AlarmSnapshot>>(_activeAlarms.Values.ToList());
    }

    public Task<AlarmSnapshot?> GetAlarmAsync(string monitorId)
    {
        _activeAlarms.TryGetValue(monitorId, out var alarm);
        return Task.FromResult(alarm);
    }

    public Task<Dictionary<string, string?>> GetLastEventStatusesAsync(IEnumerable<string> monitorIds)
        => Task.FromResult(new Dictionary<string, string?>());

    public IReadOnlyList<AlarmEvent> GetAllHistory() => _history.ToList();

    public void Clear()
    {
        _history.Clear();
        _activeAlarms.Clear();
    }
}
