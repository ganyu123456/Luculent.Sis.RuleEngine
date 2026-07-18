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
             rule_type, config_version, worker_id, shard_id,
             last_event_id, last_event_name, unit, job_id)
            VALUES
            {values}
            """;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static string FormatRow(AlarmEvent e)
    {
        // 事件流模型: 所有事件统一使用 event_type='trigger', clear_time=NULL
        // LEAD 窗口函数在查询侧配对区间，无需写入时区分 trigger/clear
        var eventType = "'trigger'";
        var clearTime = "NULL";
        var threshold = e.ThresholdValue.HasValue
            ? e.ThresholdValue.Value.ToString(CultureInfo.InvariantCulture)
            : "NULL";
        var statusName = e.StatusName != null
            ? $"'{EscapeSql(e.StatusName)}'"
            : "NULL";
        var lastEventId = e.LastEventId != null ? $"'{EscapeSql(e.LastEventId)}'" : "NULL";
        var lastEventName = e.LastEventName != null ? $"'{EscapeSql(e.LastEventName)}'" : "NULL";
        var unit = e.Unit != null ? $"'{EscapeSql(e.Unit)}'" : "NULL";
        var jobId = e.JobId != null ? $"'{EscapeSql(e.JobId)}'" : "NULL";

        return $"('{EscapeSql(e.MonitorId)}', '{EscapeSql(e.MonitorKey)}', '{EscapeSql(e.MonitorName)}', " +
               $"'{EscapeSql(e.StatusKey)}', {statusName}, " +
               $"{eventType}, '{e.OccurTime:yyyy-MM-dd HH:mm:ss.fff}', {clearTime}, " +
               $"{e.TriggerValue.ToString(CultureInfo.InvariantCulture)}, {threshold}, " +
               $"0, '{e.ConfigVersion:yyyy-MM-dd HH:mm:ss.fff}', " +
               $"'{EscapeSql(e.WorkerId)}', 0, " +
               $"{lastEventId}, {lastEventName}, {unit}, {jobId})";
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
