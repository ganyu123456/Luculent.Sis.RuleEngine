using System.Diagnostics;
using System.Globalization;
using ClickHouse.Client.ADO;
using Luculent.Sis.RuleEngine.Shared.DTOs;

namespace Luculent.Sis.RuleEngine.Master.Services;

/// <summary>
/// 历史报警查询服务。直接查询 ClickHouse alarm_events 表。
/// </summary>
public class HistoryAlarmService
{
    private readonly string _connectionString;
    private readonly ILogger<HistoryAlarmService> _logger;

    public HistoryAlarmService(IConfiguration configuration, ILogger<HistoryAlarmService> logger)
    {
        _connectionString = configuration.GetValue<string>("CLICKHOUSE_CONNECTION")
            ?? "Host=localhost;Port=8123;Database=ruleengine;Username=ruleengine;Password=RuleEngine2026!";
        _logger = logger;
        _logger.LogInformation("HistoryAlarmService 初始化, ClickHouse: {ConnString}",
            _connectionString.Replace("Password=", "Password=***"));
    }

    /// <summary>
    /// 分页查询历史报警事件（事件流模型: LEAD 窗口函数配对区间）。
    /// </summary>
    public async Task<AlarmQueryResponse> QueryAsync(AlarmQueryRequest request)
    {
        var sw = Stopwatch.StartNew();
        var where = BuildWhere(request, out var parameters);

        // 事件流模型: LEAD 计算 end_time，过滤掉空状态事件（作为区间末端被消费）
        var countSql = $"SELECT count() FROM ruleengine.alarm_events WHERE {where} AND status_key != ''";
        var dataSql = $"""
            SELECT monitor_id, monitor_key, monitor_name, status_key, status_name,
                   start_time, end_time, trigger_value, worker_id,
                   last_event_id, last_event_name, unit, job_id
            FROM (
                SELECT monitor_id, monitor_key, monitor_name, status_key, status_name,
                       occur_time AS start_time,
                       LEAD(occur_time) OVER (PARTITION BY monitor_id ORDER BY occur_time) AS end_time,
                       trigger_value, worker_id,
                       last_event_id, last_event_name, unit, job_id
                FROM ruleengine.alarm_events
                WHERE {where}
            )
            WHERE status_key != ''
            ORDER BY start_time DESC
            LIMIT {request.MaxResultCount} OFFSET {request.SkipCount}
            """;

        _logger.LogDebug("ClickHouse 查询历史(事件流): WHERE={Where}, LIMIT={Limit}", where, request.MaxResultCount);

        await using var conn = new ClickHouseConnection(_connectionString);
        await conn.OpenAsync();

        long totalCount;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = countSql;
            totalCount = Convert.ToInt64(await cmd.ExecuteScalarAsync());
        }

        var items = new List<AlarmEventDTO>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = dataSql;
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                items.Add(new AlarmEventDTO
                {
                    MonitorId = reader.GetString(0),
                    MonitorKey = reader.GetString(1),
                    MonitorName = reader.GetString(2),
                    StatusKey = reader.GetString(3),
                    StatusName = reader.IsDBNull(4) ? null : reader.GetString(4),
                    EventType = "trigger",   // 事件流模型: 所有返回行均为有效状态事件
                    OccurTime = reader.GetDateTime(5),  // start_time
                    ClearTime = reader.IsDBNull(6) ? null : reader.GetDateTime(6),  // end_time from LEAD
                    TriggerValue = reader.GetDouble(7),
                    WorkerId = reader.GetString(8),
                    LastEventId = reader.IsDBNull(9) ? null : reader.GetString(9),
                    LastEventName = reader.IsDBNull(10) ? null : reader.GetString(10),
                    Unit = reader.IsDBNull(11) ? null : reader.GetString(11),
                    JobId = reader.IsDBNull(12) ? null : reader.GetString(12),
                });
            }
        }

        sw.Stop();
        _logger.LogInformation("ClickHouse 历史查询完成(事件流): Total={TotalCount}, Returned={Count}, 耗时 {ElapsedMs}ms",
            totalCount, items.Count, sw.ElapsedMilliseconds);

        return new AlarmQueryResponse { Items = items, TotalCount = totalCount };
    }

    /// <summary>
    /// 查询单个监视项的历史报警事件。
    /// </summary>
    public async Task<AlarmQueryResponse> QueryByMonitorAsync(string monitorId, int maxCount = 200)
    {
        return await QueryAsync(new AlarmQueryRequest
        {
            MonitorIds = new List<string> { monitorId },
            MaxResultCount = maxCount,
        });
    }

    /// <summary>
    /// 闭环节点验证（事件流模型）: 查找指定时间范围内无后续 "恢复正常" 事件的报警。
    /// </summary>
    public async Task<ClosedLoopValidationResult> ValidateClosedLoopAsync(DateTime startTime, DateTime endTime)
    {
        var sw = Stopwatch.StartNew();
        // 事件流模型: 使用 LEAD 查找每个状态事件的下一个事件
        // 未关闭 = 最后一条事件的状态非空（没有后续的 status_key='' 事件）
        var sql = $"""
            SELECT
                monitor_id,
                monitor_key,
                monitor_name,
                status_key,
                max(start_time) AS last_trigger,
                max(next_status) AS next_status
            FROM (
                SELECT monitor_id, monitor_key, monitor_name, status_key,
                       occur_time AS start_time,
                       LEAD(status_key) OVER (PARTITION BY monitor_id ORDER BY occur_time) AS next_status
                FROM ruleengine.alarm_events
                WHERE occur_time >= '{startTime:yyyy-MM-dd HH:mm:ss}'
                  AND occur_time <= '{endTime:yyyy-MM-dd HH:mm:ss}'
            )
            WHERE status_key != ''
            GROUP BY monitor_id, monitor_key, monitor_name, status_key
            HAVING next_status IS NULL OR next_status != ''
            ORDER BY last_trigger DESC
            LIMIT 500
            """;

        _logger.LogDebug("闭环验证查询(事件流): {StartTime} ~ {EndTime}", startTime, endTime);

        await using var conn = new ClickHouseConnection(_connectionString);
        await conn.OpenAsync();

        var openEvents = new List<OpenAlarmInfo>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = sql;
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var hasNextStatus = !reader.IsDBNull(4) && reader.GetString(4) != "";
                openEvents.Add(new OpenAlarmInfo
                {
                    MonitorId = reader.GetString(0),
                    MonitorKey = reader.GetString(1),
                    MonitorName = reader.GetString(2),
                    StatusKey = reader.GetString(3),
                    TriggerCount = hasNextStatus ? 1 : 1,
                    ClearCount = hasNextStatus ? 0 : 1,
                    FirstTrigger = reader.GetDateTime(4),
                    LastClear = null,
                });
            }
        }

        // 总览统计
        var statsSql = $"""
            SELECT
                uniqExact(monitor_id) AS affected_monitors,
                countIf(event_type = 'trigger') AS total_events
            FROM ruleengine.alarm_events
            WHERE occur_time >= '{startTime:yyyy-MM-dd HH:mm:ss}'
              AND occur_time <= '{endTime:yyyy-MM-dd HH:mm:ss}'
              AND status_key != ''
            """;

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = statsSql;
            await using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                sw.Stop();
                var affectedMonitors = Convert.ToInt64(reader.GetValue(0));
                var totalEvents = Convert.ToInt64(reader.GetValue(1));
                var result = new ClosedLoopValidationResult
                {
                    TotalTriggers = totalEvents,
                    TotalClears = totalEvents - openEvents.Count,
                    AffectedMonitors = affectedMonitors,
                    OpenEvents = openEvents,
                    IsClosedLoop = openEvents.Count == 0,
                };
                _logger.LogInformation("闭环验证完成(事件流): 状态事件 {TotalEvents}, 未关闭 {OpenCount}, 耗时 {ElapsedMs}ms",
                    result.TotalTriggers, result.OpenEvents.Count, sw.ElapsedMilliseconds);
                return result;
            }
        }

        sw.Stop();
        return new ClosedLoopValidationResult { OpenEvents = openEvents };
    }

    private static string BuildWhere(AlarmQueryRequest request, out List<object> _)
    {
        _ = new List<object>();
        var conditions = new List<string>();

        if (request.MonitorIds.Count > 0)
        {
            var ids = string.Join(", ", request.MonitorIds.Select(id => $"'{EscapeSql(id)}'"));
            conditions.Add($"monitor_id IN ({ids})");
        }

        if (request.MonitorKeys?.Count > 0)
        {
            var keys = string.Join(", ", request.MonitorKeys.Select(k => $"'{EscapeSql(k)}'"));
            conditions.Add($"monitor_key IN ({keys})");
        }

        if (request.StartTime.HasValue)
            conditions.Add($"occur_time >= '{request.StartTime.Value:yyyy-MM-dd HH:mm:ss}'");

        if (request.EndTime.HasValue)
            conditions.Add($"occur_time <= '{request.EndTime.Value:yyyy-MM-dd HH:mm:ss}'");

        if (request.StatusKeys?.Count > 0)
        {
            var keys = string.Join(", ", request.StatusKeys.Select(k => $"'{EscapeSql(k)}'"));
            conditions.Add($"status_key IN ({keys})");
        }

        // 事件流模型: 不再按 event_type 过滤，所有有效事件均为状态变更记录
        // status_key != '' 在查询外层过滤
        return conditions.Count > 0 ? string.Join(" AND ", conditions) : "1=1";
    }

    private static string EscapeSql(string value) => value.Replace("\\", "\\\\").Replace("'", "\\'");
}

public class ClosedLoopValidationResult
{
    public long TotalTriggers { get; set; }
    public long TotalClears { get; set; }
    public long AffectedMonitors { get; set; }
    public bool IsClosedLoop { get; set; }
    public List<OpenAlarmInfo> OpenEvents { get; set; } = new();
}

public class OpenAlarmInfo
{
    public string MonitorId { get; set; } = string.Empty;
    public string MonitorKey { get; set; } = string.Empty;
    public string MonitorName { get; set; } = string.Empty;
    public string StatusKey { get; set; } = string.Empty;
    public long TriggerCount { get; set; }
    public long ClearCount { get; set; }
    public DateTime FirstTrigger { get; set; }
    public DateTime? LastClear { get; set; }
}
