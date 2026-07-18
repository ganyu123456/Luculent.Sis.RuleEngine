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
    /// 分页查询历史报警事件，支持多条件过滤。
    /// </summary>
    public async Task<AlarmQueryResponse> QueryAsync(AlarmQueryRequest request)
    {
        var sw = Stopwatch.StartNew();
        var where = BuildWhere(request, out var parameters);

        var countSql = $"SELECT count() FROM ruleengine.alarm_events WHERE {where}";
        var dataSql = $"""
            SELECT monitor_id, monitor_key, monitor_name, status_key, status_name,
                   event_type, occur_time, clear_time, trigger_value, worker_id,
                   last_event_id, last_event_name, unit, job_id
            FROM ruleengine.alarm_events
            WHERE {where}
            ORDER BY occur_time DESC
            LIMIT {request.MaxResultCount} OFFSET {request.SkipCount}
            """;

        _logger.LogDebug("ClickHouse 查询历史: WHERE={Where}, LIMIT={Limit}", where, request.MaxResultCount);

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
                    EventType = reader.GetString(5),
                    OccurTime = reader.GetDateTime(6),
                    ClearTime = reader.IsDBNull(7) ? null : reader.GetDateTime(7),
                    TriggerValue = reader.GetDouble(8),
                    WorkerId = reader.GetString(9),
                    LastEventId = reader.IsDBNull(10) ? null : reader.GetString(10),
                    LastEventName = reader.IsDBNull(11) ? null : reader.GetString(11),
                    Unit = reader.IsDBNull(12) ? null : reader.GetString(12),
                    JobId = reader.IsDBNull(13) ? null : reader.GetString(13),
                });
            }
        }

        sw.Stop();
        _logger.LogInformation("ClickHouse 历史查询完成: Total={TotalCount}, Returned={Count}, 耗时 {ElapsedMs}ms",
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
    /// 闭环节点验证：查找指定时间范围内有触发但无对应消除的报警事件。
    /// 对标 MonitorCenter 的闭环事件校验逻辑。
    /// </summary>
    public async Task<ClosedLoopValidationResult> ValidateClosedLoopAsync(DateTime startTime, DateTime endTime)
    {
        var sw = Stopwatch.StartNew();
        var sql = $"""
            SELECT
                monitor_id,
                monitor_key,
                monitor_name,
                status_key,
                countIf(event_type = 'trigger') AS trigger_count,
                countIf(event_type = 'clear') AS clear_count,
                minIf(occur_time, event_type = 'trigger') AS first_trigger,
                maxIf(occur_time, event_type = 'clear') AS last_clear
            FROM ruleengine.alarm_events
            WHERE occur_time >= '{startTime:yyyy-MM-dd HH:mm:ss}'
              AND occur_time <= '{endTime:yyyy-MM-dd HH:mm:ss}'
            GROUP BY monitor_id, monitor_key, monitor_name, status_key
            HAVING trigger_count > clear_count
            ORDER BY trigger_count DESC
            LIMIT 500
            """;

        _logger.LogDebug("闭环验证查询: {StartTime} ~ {EndTime}", startTime, endTime);

        await using var conn = new ClickHouseConnection(_connectionString);
        await conn.OpenAsync();

        var openEvents = new List<OpenAlarmInfo>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = sql;
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var clearCount = Convert.ToInt64(reader.GetValue(5));
                openEvents.Add(new OpenAlarmInfo
                {
                    MonitorId = reader.GetString(0),
                    MonitorKey = reader.GetString(1),
                    MonitorName = reader.GetString(2),
                    StatusKey = reader.GetString(3),
                    TriggerCount = Convert.ToInt64(reader.GetValue(4)),
                    ClearCount = clearCount,
                    FirstTrigger = reader.GetDateTime(6),
                    LastClear = clearCount > 0 && !reader.IsDBNull(7) ? reader.GetDateTime(7) : null,
                });
            }
        }

        // 总览统计
        var statsSql = $"""
            SELECT
                countIf(event_type = 'trigger') AS total_triggers,
                countIf(event_type = 'clear') AS total_clears,
                uniq(monitor_id) AS affected_monitors
            FROM ruleengine.alarm_events
            WHERE occur_time >= '{startTime:yyyy-MM-dd HH:mm:ss}'
              AND occur_time <= '{endTime:yyyy-MM-dd HH:mm:ss}'
            """;

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = statsSql;
            await using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                sw.Stop();
                var result = new ClosedLoopValidationResult
                {
                    TotalTriggers = Convert.ToInt64(reader.GetValue(0)),
                    TotalClears = Convert.ToInt64(reader.GetValue(1)),
                    AffectedMonitors = Convert.ToInt64(reader.GetValue(2)),
                    OpenEvents = openEvents,
                    IsClosedLoop = Convert.ToInt64(reader.GetValue(0)) == Convert.ToInt64(reader.GetValue(1)),
                };
                _logger.LogInformation("闭环验证完成: 触发 {Triggers}, 消除 {Clears}, 未关闭 {OpenCount}, 耗时 {ElapsedMs}ms",
                    result.TotalTriggers, result.TotalClears, result.OpenEvents.Count, sw.ElapsedMilliseconds);
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

        if (request.EventTypes?.Count > 0)
        {
            var types = string.Join(", ", request.EventTypes.Select(t => $"'{EscapeSql(t)}'"));
            conditions.Add($"event_type IN ({types})");
        }

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
