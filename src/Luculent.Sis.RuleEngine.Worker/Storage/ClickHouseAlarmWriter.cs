using System.Globalization;
using ClickHouse.Client.ADO;
using Luculent.Sis.RuleEngine.Shared.Enums;
using Luculent.Sis.RuleEngine.Shared.Interfaces;
using Luculent.Sis.RuleEngine.Shared.Models;
using Microsoft.Extensions.Logging;

namespace Luculent.Sis.RuleEngine.Worker.Storage;

/// <summary>
/// 基于 ClickHouse 的历史报警写入实现。
/// 生产环境下处理 WriteHistoryAlarmAsync。
/// </summary>
public class ClickHouseAlarmWriter : IAlarmWriter
{
    private readonly string _connectionString;
    private readonly ILogger<ClickHouseAlarmWriter> _logger;

    public ClickHouseAlarmWriter(string connectionString, ILogger<ClickHouseAlarmWriter> logger)
    {
        _connectionString = connectionString;
        _logger = logger;
    }

    public Task WriteRealtimeAlarmAsync(AlarmSnapshot alarm) => Task.CompletedTask;

    public Task ClearRealtimeAlarmAsync(string monitorId) => Task.CompletedTask;

    public async Task WriteHistoryAlarmAsync(AlarmEvent alarmEvent)
    {
        try
        {
            await using var conn = new ClickHouseConnection(_connectionString);
            await conn.OpenAsync();

            var eventType = alarmEvent.EventType == EventType.Trigger ? "'trigger'" : "'clear'";
            var clearTime = alarmEvent.ClearTime.HasValue
                ? $"'{alarmEvent.ClearTime.Value:yyyy-MM-dd HH:mm:ss.fff}'"
                : "NULL";
            var threshold = alarmEvent.ThresholdValue.HasValue
                ? alarmEvent.ThresholdValue.Value.ToString(CultureInfo.InvariantCulture)
                : "NULL";
            var statusName = alarmEvent.StatusName != null
                ? $"'{EscapeSql(alarmEvent.StatusName)}'"
                : "NULL";

            var sql = $"""
                INSERT INTO ruleengine.alarm_events
                (monitor_id, monitor_key, monitor_name, status_key, status_name,
                 event_type, occur_time, clear_time, trigger_value, threshold_value,
                 rule_type, config_version, worker_id, shard_id)
                VALUES
                ('{EscapeSql(alarmEvent.MonitorId)}', '{EscapeSql(alarmEvent.MonitorKey)}', '{EscapeSql(alarmEvent.MonitorName)}',
                 '{EscapeSql(alarmEvent.StatusKey)}', {statusName},
                 {eventType}, '{alarmEvent.OccurTime:yyyy-MM-dd HH:mm:ss.fff}', {clearTime},
                 {alarmEvent.TriggerValue.ToString(CultureInfo.InvariantCulture)}, {threshold},
                 0, '{alarmEvent.ConfigVersion:yyyy-MM-dd HH:mm:ss.fff}',
                 '{EscapeSql(alarmEvent.WorkerId)}', 0)
                """;

            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync();

            _logger.LogDebug("ClickHouse 写入历史报警: {MonitorId} {EventType} {StatusKey}",
                alarmEvent.MonitorId, alarmEvent.EventType, alarmEvent.StatusKey);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ClickHouse 写入历史报警失败: {MonitorId}", alarmEvent.MonitorId);
            throw;
        }
    }

    public Task<IReadOnlyList<AlarmSnapshot>> GetActiveAlarmsAsync()
        => Task.FromResult<IReadOnlyList<AlarmSnapshot>>(Array.Empty<AlarmSnapshot>());

    public Task<AlarmSnapshot?> GetAlarmAsync(string monitorId)
        => Task.FromResult<AlarmSnapshot?>(null);

    private static string EscapeSql(string value) => value.Replace("\\", "\\\\").Replace("'", "\\'");
}
