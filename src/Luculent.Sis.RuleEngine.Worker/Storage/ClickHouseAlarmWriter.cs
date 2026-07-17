using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;
using ClickHouse.Client.ADO;
using Luculent.Sis.RuleEngine.Shared.Enums;
using Luculent.Sis.RuleEngine.Shared.Interfaces;
using Luculent.Sis.RuleEngine.Shared.Models;
using Microsoft.Extensions.Logging;

namespace Luculent.Sis.RuleEngine.Worker.Storage;

/// <summary>
/// 基于 ClickHouse 的历史报警写入实现。
/// 使用后台 Channel + 批量 INSERT 减少连接开销。
/// </summary>
public class ClickHouseAlarmWriter : IAlarmWriter, IAsyncDisposable
{
    private readonly string _connectionString;
    private readonly ILogger<ClickHouseAlarmWriter> _logger;
    private readonly Channel<AlarmEvent> _channel;
    private readonly CancellationTokenSource _cts;
    private readonly Task _flushTask;

    private long _totalWritten;
    private long _totalDropped;
    private long _totalErrors;

    public ClickHouseAlarmWriter(string connectionString, ILogger<ClickHouseAlarmWriter> logger)
    {
        _connectionString = connectionString;
        _logger = logger;

        _channel = Channel.CreateBounded<AlarmEvent>(new BoundedChannelOptions(10000)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });
        _cts = new CancellationTokenSource();
        _flushTask = Task.Run(() => FlushLoopAsync(_cts.Token));
    }

    public Task WriteRealtimeAlarmAsync(AlarmSnapshot alarm) => Task.CompletedTask;

    public Task ClearRealtimeAlarmAsync(string monitorId) => Task.CompletedTask;

    public async Task WriteHistoryAlarmAsync(AlarmEvent alarmEvent)
    {
        if (!_channel.Writer.TryWrite(alarmEvent))
        {
            var dropped = Interlocked.Increment(ref _totalDropped);
            if (dropped % 1000 == 1)
                _logger.LogWarning("ClickHouse 写入队列满，已丢弃 {Dropped} 条", dropped);
        }
    }

    public Task<IReadOnlyList<AlarmSnapshot>> GetActiveAlarmsAsync()
        => Task.FromResult<IReadOnlyList<AlarmSnapshot>>(Array.Empty<AlarmSnapshot>());

    public Task<AlarmSnapshot?> GetAlarmAsync(string monitorId)
        => Task.FromResult<AlarmSnapshot?>(null);

    private async Task FlushLoopAsync(CancellationToken ct)
    {
        var batch = new List<AlarmEvent>(500);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _channel.Reader.WaitToReadAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            // 收集一批数据
            while (batch.Count < 500 && _channel.Reader.TryRead(out var item))
                batch.Add(item);

            if (batch.Count == 0) continue;

            try
            {
                var sw = Stopwatch.StartNew();
                await WriteBatchAsync(batch, ct);
                sw.Stop();

                var written = Interlocked.Add(ref _totalWritten, batch.Count);
                _logger.LogDebug("ClickHouse 批量写入: {Count} 条, 耗时 {ElapsedMs}ms, 累计 {TotalWritten} 条",
                    batch.Count, sw.ElapsedMilliseconds, written);
            }
            catch (Exception ex)
            {
                var errors = Interlocked.Increment(ref _totalErrors);
                _logger.LogError(ex, "ClickHouse 批量写入失败 #{ErrorCount}: {Count} 条事件丢失, 错误: {Message}",
                    errors, batch.Count, ex.Message);
            }

            batch.Clear();
        }

        // 退出前刷新残留数据
        if (batch.Count > 0)
        {
            try
            {
                await WriteBatchAsync(batch, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ClickHouse 退出刷新失败: {Count} 条", batch.Count);
            }
        }
    }

    private async Task WriteBatchAsync(List<AlarmEvent> events, CancellationToken ct)
    {
        await using var conn = new ClickHouseConnection(_connectionString);
        await conn.OpenAsync(ct);

        var values = string.Join(",\n", events.Select(FormatRow));
        var sql = $"""
            INSERT INTO ruleengine.alarm_events
            (monitor_id, monitor_key, monitor_name, status_key, status_name,
             event_type, occur_time, clear_time, trigger_value, threshold_value,
             rule_type, config_version, worker_id, shard_id)
            VALUES
            {values}
            """;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);

        // 对 clear 事件，同步更新对应 trigger 事件的 clear_time
        foreach (var e in events)
        {
            if (e.EventType != EventType.Clear) continue;

            try
            {
                using var updateCmd = conn.CreateCommand();
                updateCmd.CommandText = $"""
                    ALTER TABLE ruleengine.alarm_events
                    UPDATE clear_time = '{e.OccurTime:yyyy-MM-dd HH:mm:ss.fff}'
                    WHERE monitor_id = '{EscapeSql(e.MonitorId)}'
                      AND status_key = '{EscapeSql(e.StatusKey)}'
                      AND event_type = 'trigger'
                      AND clear_time IS NULL
                      AND occur_time >= '{e.OccurTime.AddHours(-1):yyyy-MM-dd HH:mm:ss.fff}'
                    SETTINGS mutations_sync = 1
                    """;
                await updateCmd.ExecuteNonQueryAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "更新 trigger clear_time 失败: {MonitorId}/{StatusKey}",
                    e.MonitorId, e.StatusKey);
            }
        }
    }

    private static string FormatRow(AlarmEvent e)
    {
        var eventType = e.EventType == EventType.Trigger ? "'trigger'" : "'clear'";
        var clearTime = e.ClearTime.HasValue
            ? $"'{e.ClearTime.Value:yyyy-MM-dd HH:mm:ss.fff}'"
            : "NULL";
        var threshold = e.ThresholdValue.HasValue
            ? e.ThresholdValue.Value.ToString(CultureInfo.InvariantCulture)
            : "NULL";
        var statusName = e.StatusName != null
            ? $"'{EscapeSql(e.StatusName)}'"
            : "NULL";

        return $"('{EscapeSql(e.MonitorId)}', '{EscapeSql(e.MonitorKey)}', '{EscapeSql(e.MonitorName)}', " +
               $"'{EscapeSql(e.StatusKey)}', {statusName}, " +
               $"{eventType}, '{e.OccurTime:yyyy-MM-dd HH:mm:ss.fff}', {clearTime}, " +
               $"{e.TriggerValue.ToString(CultureInfo.InvariantCulture)}, {threshold}, " +
               $"0, '{e.ConfigVersion:yyyy-MM-dd HH:mm:ss.fff}', " +
               $"'{EscapeSql(e.WorkerId)}', 0)";
    }

    private static string EscapeSql(string value) => value.Replace("\\", "\\\\").Replace("'", "\\'");

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        _cts.Cancel();

        try { await _flushTask; } catch (OperationCanceledException) { }

        _logger.LogInformation(
            "ClickHouseAlarmWriter 已释放: 累计写入 {TotalWritten}, 丢弃 {TotalDropped}, 错误批次 {TotalErrors}",
            _totalWritten, _totalDropped, _totalErrors);

        _cts.Dispose();
    }
}
