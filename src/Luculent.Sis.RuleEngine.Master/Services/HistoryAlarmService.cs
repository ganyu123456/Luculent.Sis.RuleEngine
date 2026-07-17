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
            ?? "Host=localhost;Port=8123;Database=ruleengine";
        _logger = logger;
    }

    /// <summary>
    /// 分页查询历史报警事件，支持多条件过滤。
    /// </summary>
    public async Task<AlarmQueryResponse> QueryAsync(AlarmQueryRequest request)
    {
        var where = BuildWhere(request, out var parameters);

        var countSql = $"SELECT count() FROM ruleengine.alarm_events WHERE {where}";
        var dataSql = $"""
            SELECT monitor_id, monitor_key, monitor_name, status_key, status_name,
                   event_type, occur_time, clear_time, trigger_value, worker_id
            FROM ruleengine.alarm_events
            WHERE {where}
            ORDER BY occur_time DESC
            LIMIT {request.MaxResultCount} OFFSET {request.SkipCount}
            """;

        await using var conn = new ClickHouseConnection(_connectionString);
        await conn.OpenAsync();

        long totalCount;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = countSql;
            totalCount = (long)(await cmd.ExecuteScalarAsync())!;
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
                });
            }
        }

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

        await using var conn = new ClickHouseConnection(_connectionString);
        await conn.OpenAsync();

        var openEvents = new List<OpenAlarmInfo>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = sql;
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                openEvents.Add(new OpenAlarmInfo
                {
                    MonitorId = reader.GetString(0),
                    MonitorKey = reader.GetString(1),
                    MonitorName = reader.GetString(2),
                    StatusKey = reader.GetString(3),
                    TriggerCount = reader.GetInt64(4),
                    ClearCount = reader.GetInt64(5),
                    FirstTrigger = reader.GetDateTime(6),
                    LastClear = reader.IsDBNull(7) ? null : reader.GetDateTime(7),
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
                return new ClosedLoopValidationResult
                {
                    TotalTriggers = reader.GetInt64(0),
                    TotalClears = reader.GetInt64(1),
                    AffectedMonitors = reader.GetInt64(2),
                    OpenEvents = openEvents,
                    IsClosedLoop = reader.GetInt64(0) == reader.GetInt64(1),
                };
            }
        }

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

        if (request.StartTime.HasValue)
            conditions.Add($"occur_time >= '{request.StartTime.Value:yyyy-MM-dd HH:mm:ss}'");

        if (request.EndTime.HasValue)
            conditions.Add($"occur_time <= '{request.EndTime.Value:yyyy-MM-dd HH:mm:ss}'");

        if (request.StatusKeys?.Count > 0)
        {
            var keys = string.Join(", ", request.StatusKeys.Select(k => $"'{EscapeSql(k)}'"));
            conditions.Add($"status_key IN ({keys})");
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
