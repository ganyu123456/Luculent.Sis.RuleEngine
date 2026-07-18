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
    /// 分页查询历史报警事件（事件流模型: C# 层配对相邻事件计算 end_time）。
    /// ClickHouse 24.10 不支持 LEAD 窗口函数，改为应用层计算区间结束时间。
    /// </summary>
    public async Task<AlarmQueryResponse> QueryAsync(AlarmQueryRequest request)
    {
        var sw = Stopwatch.StartNew();
        var where = BuildWhere(request, out var parameters);

        // 事件流配对需要完整的事件链，因此数据查询始终包含全部事件（含 status_key=''）
        // containNull 的过滤在 C# 配对完成后执行
        var nonEmptyFilter = request.ContainNull ? "" : " AND status_key != ''";
        var countSql = $"SELECT count() FROM ruleengine.alarm_events WHERE {where}{nonEmptyFilter}";

        // 当 containNull=false 时多取一些行，以补偿 C# 层过滤空状态事件的损耗
        var fetchMultiplier = request.ContainNull ? 1 : 3;
        var fetchLimit = (request.MaxResultCount + request.SkipCount) * fetchMultiplier + 200;
        var dataSql = $"""
            SELECT monitor_id, monitor_key, monitor_name, status_key, status_name,
                   occur_time, trigger_value, worker_id,
                   last_event_id, last_event_name, unit, job_id
            FROM ruleengine.alarm_events
            WHERE {where}
            ORDER BY occur_time DESC
            LIMIT {fetchLimit}
            """;

        _logger.LogDebug("ClickHouse 查询历史(事件流): WHERE={Where}, LIMIT={Limit}", where, fetchLimit);

        await using var conn = new ClickHouseConnection(_connectionString);
        await conn.OpenAsync();

        long totalCount;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = countSql;
            totalCount = Convert.ToInt64(await cmd.ExecuteScalarAsync());
        }

        var rawEvents = new List<AlarmEventDTO>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = dataSql;
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                rawEvents.Add(new AlarmEventDTO
                {
                    MonitorId = reader.GetString(0),
                    MonitorKey = reader.GetString(1),
                    MonitorName = reader.GetString(2),
                    StatusKey = reader.GetString(3),
                    StatusName = reader.IsDBNull(4) ? null : reader.GetString(4),
                    EventType = "trigger",
                    OccurTime = reader.GetDateTime(5),
                    TriggerValue = reader.GetDouble(6),
                    WorkerId = reader.GetString(7),
                    LastEventId = reader.IsDBNull(8) ? null : reader.GetString(8),
                    LastEventName = reader.IsDBNull(9) ? null : reader.GetString(9),
                    Unit = reader.IsDBNull(10) ? null : reader.GetString(10),
                    JobId = reader.IsDBNull(11) ? null : reader.GetString(11),
                });
            }
        }

        // 事件流配对: 对每个 monitor 的事件按时间升序排列，相邻配对计算 end_time
        var eventsByMonitor = rawEvents
            .GroupBy(e => e.MonitorId)
            .ToDictionary(g => g.Key, g => g.OrderBy(e => e.OccurTime).ToList());

        foreach (var (_, events) in eventsByMonitor)
        {
            for (int i = 0; i < events.Count - 1; i++)
                events[i].ClearTime = events[i + 1].OccurTime;
            // 最后一个事件无下一个事件配对，ClearTime 保持 null（表示事件仍在持续）
        }

        // 展开、按 OccurTime 降序排列、containNull 过滤、分页
        var filtered = request.ContainNull
            ? rawEvents
            : rawEvents.Where(e => !string.IsNullOrEmpty(e.StatusKey)).ToList();

        var items = filtered
            .OrderByDescending(e => e.OccurTime)
            .Skip(request.SkipCount)
            .Take(request.MaxResultCount)
            .ToList();

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
    /// 闭环节点验证（事件流模型）: 查找指定时间范围内无后续 StatusKey="" 事件的报警。
    /// ClickHouse 24.10 不支持 LEAD，改为 C# 层配对。
    /// </summary>
    public async Task<ClosedLoopValidationResult> ValidateClosedLoopAsync(DateTime startTime, DateTime endTime)
    {
        var sw = Stopwatch.StartNew();

        var sql = $"""
            SELECT monitor_id, monitor_key, monitor_name, status_key,
                   occur_time
            FROM ruleengine.alarm_events
            WHERE occur_time >= '{startTime:yyyy-MM-dd HH:mm:ss}'
              AND occur_time <= '{endTime:yyyy-MM-dd HH:mm:ss}'
            ORDER BY monitor_id, occur_time
            LIMIT 10000
            """;

        _logger.LogDebug("闭环验证查询(事件流): {StartTime} ~ {EndTime}", startTime, endTime);

        await using var conn = new ClickHouseConnection(_connectionString);
        await conn.OpenAsync();

        var raw = new List<(string MonitorId, string MonitorKey, string MonitorName, string StatusKey, DateTime OccurTime)>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = sql;
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                raw.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2),
                          reader.GetString(3), reader.GetDateTime(4)));
            }
        }

        // 事件流配对: 对每个 monitor，检查最后一条 status_key != '' 事件是否还有后续空状态事件
        var openEvents = new List<OpenAlarmInfo>();
        var monitorLastStatus = new Dictionary<string, (string StatusKey, string MonitorKey, string MonitorName, DateTime OccurTime)>();
        foreach (var (monitorId, monitorKey, monitorName, statusKey, occurTime) in raw)
        {
            monitorLastStatus[monitorId] = (statusKey, monitorKey, monitorName, occurTime);
        }

        foreach (var (monitorId, info) in monitorLastStatus)
        {
            if (!string.IsNullOrEmpty(info.StatusKey))
            {
                openEvents.Add(new OpenAlarmInfo
                {
                    MonitorId = monitorId,
                    MonitorKey = info.MonitorKey,
                    MonitorName = info.MonitorName,
                    StatusKey = info.StatusKey,
                    TriggerCount = 1,
                    ClearCount = 0,
                    FirstTrigger = info.OccurTime,
                    LastClear = null,
                });
            }
        }

        openEvents = openEvents.OrderByDescending(e => e.FirstTrigger).Take(500).ToList();

        // 总览统计
        var statsSql = $"""
            SELECT
                uniqExact(monitor_id) AS affected_monitors,
                countIf(status_key != '') AS total_events
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
