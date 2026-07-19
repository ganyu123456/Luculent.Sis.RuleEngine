using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;
using ClickHouse.Client.ADO;
using Luculent.Sis.RuleEngine.Shared.Interfaces;
using Luculent.Sis.RuleEngine.Shared.Models;
using Microsoft.Extensions.Logging;

namespace Luculent.Sis.RuleEngine.Worker.Storage;

/// <summary>
/// 基于 ClickHouse 的历史报警写入实现。
/// 使用后台 Channel + 批量 INSERT + 复用连接 + 定时刷新。
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

    // ClickHouse 最佳实践: 每批 >=1000 行，理想 10,000+。这里用 5000 平衡内存和性能。
    private const int MaxBatchSize = 5000;

    // 定时刷新: 最多 1 秒写一次，保证数据时效性
    private static readonly TimeSpan MaxFlushInterval = TimeSpan.FromSeconds(1);

    // 缓冲区容量 (生产端满时丢弃最旧事件)
    private const int ChannelCapacity = 20000;

    public ClickHouseAlarmWriter(string connectionString, ILogger<ClickHouseAlarmWriter> logger)
    {
        _connectionString = connectionString;
        _logger = logger;

        _channel = Channel.CreateBounded<AlarmEvent>(new BoundedChannelOptions(ChannelCapacity)
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

    public async Task<Dictionary<string, string?>> GetLastEventStatusesAsync(IEnumerable<string> monitorIds)
    {
        var idList = monitorIds.ToList();
        if (idList.Count == 0)
            return new Dictionary<string, string?>();

        var result = new Dictionary<string, string?>();
        const int batchSize = 500;

        for (int offset = 0; offset < idList.Count; offset += batchSize)
        {
            var batch = idList.Skip(offset).Take(batchSize).ToList();
            var inClause = string.Join(", ", batch.Select(id => $"'{EscapeSql(id)}'"));
            var sql = $"""
                SELECT monitor_id, status_key
                FROM ruleengine.alarm_events
                WHERE monitor_id IN ({inClause})
                ORDER BY occur_time DESC
                LIMIT 1 BY monitor_id
                """;

            try
            {
                await using var conn = new ClickHouseConnection(_connectionString);
                await conn.OpenAsync();

                using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;

                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var monitorId = reader.GetString(0);
                    var statusKey = reader.IsDBNull(1) ? null : reader.GetString(1);
                    result[monitorId] = statusKey;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Worker 状态恢复 ClickHouse 批次失败: offset={Offset}, count={Count}",
                    offset, batch.Count);
            }
        }

        // 补全: 查询无结果的监视项 → 正常态
        foreach (var id in idList)
        {
            if (!result.ContainsKey(id))
                result[id] = "";
        }

        return result;
    }

    /// <summary>
    /// 批量刷新循环: 攒够 MaxBatchSize 条 OR 距上次写入超过 MaxFlushInterval 则触发 INSERT。
    /// 复用单条 ClickHouse 连接，异常时自动重连重试。
    /// </summary>
    private async Task FlushLoopAsync(CancellationToken ct)
    {
        var batch = new List<AlarmEvent>(MaxBatchSize);
        var lastFlush = DateTime.UtcNow;

        var conn = new ClickHouseConnection(_connectionString);
        await conn.OpenAsync(ct);

        // 启用服务端异步插入: 小 INSERT 由 ClickHouse 合并为大 part 后再写盘
        // 配合定时刷新 + 大批量，双重保障降低 merge 压力
        await EnableAsyncInsertAsync(conn, ct);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // 计算剩余等待时间: 距离下次定时刷新还剩多少 ms
                    var remainingMs = Math.Max(1,
                        (int)(MaxFlushInterval - (DateTime.UtcNow - lastFlush)).TotalMilliseconds);

                    // 等待首个事件到达 OR 定时刷新时间到
                    using var timeoutCts = new CancellationTokenSource(remainingMs);
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
                    try
                    {
                        await _channel.Reader.WaitToReadAsync(linkedCts.Token);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        // 定时器到期: 刷新缓冲区中已有的数据
                    }

                    // 尽可能多地收集事件 (上限 MaxBatchSize)
                    while (batch.Count < MaxBatchSize && _channel.Reader.TryRead(out var item))
                        batch.Add(item);

                    // 触发条件: batch 满 OR 定时器到期且有数据
                    var sinceLast = DateTime.UtcNow - lastFlush;
                    if (batch.Count >= MaxBatchSize || (batch.Count > 0 && sinceLast >= MaxFlushInterval))
                    {
                        var sw = Stopwatch.StartNew();
                        await WriteBatchAsync(conn, batch, ct);
                        sw.Stop();

                        var written = Interlocked.Add(ref _totalWritten, batch.Count);
                        _logger.LogDebug("ClickHouse 写入: {Count} 条, 耗时 {ElapsedMs}ms, 累计 {TotalWritten}",
                            batch.Count, sw.ElapsedMilliseconds, written);
                        batch.Clear();
                        lastFlush = DateTime.UtcNow;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    var errors = Interlocked.Increment(ref _totalErrors);
                    _logger.LogError(ex, "ClickHouse 写入失败 #{ErrorCount}: {Count} 条, 尝试重连重试",
                        errors, batch.Count);

                    // 重连
                    try { await conn.DisposeAsync(); } catch { /* ignore */ }
                    try
                    {
                        conn = new ClickHouseConnection(_connectionString);
                        await conn.OpenAsync(CancellationToken.None);
                        await EnableAsyncInsertAsync(conn, CancellationToken.None);

                        if (batch.Count > 0)
                        {
                            await WriteBatchAsync(conn, batch, CancellationToken.None);
                            Interlocked.Add(ref _totalWritten, batch.Count);
                        }
                    }
                    catch (Exception retryEx)
                    {
                        _logger.LogError(retryEx, "重连重试失败: {Count} 条事件丢失", batch.Count);
                    }

                    batch.Clear();
                    lastFlush = DateTime.UtcNow;
                }
            }

            // 退出前最终刷新
            if (batch.Count > 0)
            {
                try
                {
                    await WriteBatchAsync(conn, batch, CancellationToken.None);
                    Interlocked.Add(ref _totalWritten, batch.Count);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "ClickHouse 退出刷新失败: {Count} 条", batch.Count);
                }
            }
        }
        finally
        {
            await conn.DisposeAsync();
        }
    }

    private static async Task WriteBatchAsync(ClickHouseConnection conn, List<AlarmEvent> events, CancellationToken ct)
    {
        var values = string.Join(",\n", events.Select(FormatRow));
        var sql = $"""
            INSERT INTO ruleengine.alarm_events
            (monitor_id, monitor_key, monitor_name, status_key, status_name,
             occur_time, trigger_value, threshold_value,
             rule_type, config_version, worker_id, shard_id,
             last_event_id, last_event_name, unit, job_id,
             max_value, min_value)
            VALUES
            {values}
            """;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static string FormatRow(AlarmEvent e)
    {
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

        var maxValue = e.MaxValue.HasValue
            ? e.MaxValue.Value.ToString(CultureInfo.InvariantCulture)
            : "NULL";
        var minValue = e.MinValue.HasValue
            ? e.MinValue.Value.ToString(CultureInfo.InvariantCulture)
            : "NULL";

        return $"('{EscapeSql(e.MonitorId)}', '{EscapeSql(e.MonitorKey)}', '{EscapeSql(e.MonitorName)}', " +
               $"'{EscapeSql(e.StatusKey)}', {statusName}, " +
               $"'{e.OccurTime:yyyy-MM-dd HH:mm:ss.fff}', " +
               $"{e.TriggerValue.ToString(CultureInfo.InvariantCulture)}, {threshold}, " +
               $"0, '{e.ConfigVersion:yyyy-MM-dd HH:mm:ss.fff}', " +
               $"'{EscapeSql(e.WorkerId)}', 0, " +
               $"{lastEventId}, {lastEventName}, {unit}, {jobId}, " +
               $"{maxValue}, {minValue})";
    }

    private static async Task EnableAsyncInsertAsync(ClickHouseConnection conn, CancellationToken ct)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SET async_insert = 1; SET wait_for_async_insert = 1;";
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch
        {
            // 低版本 ClickHouse 可能不支持，忽略即可
        }
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
