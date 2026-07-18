using System.Text.Json;
using Luculent.Sis.RuleEngine.Shared.Interfaces;
using Luculent.Sis.RuleEngine.Shared.Models;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Luculent.Sis.RuleEngine.Worker.Storage;

/// <summary>
/// 基于 Redis 的实时报警写入实现。
/// 使用 Hash 存储报警详情，Set 维护活跃报警索引。
/// 生产环境下处理 WriteRealtimeAlarm / ClearRealtimeAlarm / GetAlarm / GetActiveAlarms。
/// </summary>
public class RedisAlarmWriter : IAlarmWriter, IDisposable
{
    private const string ActiveAlarmsSetKey = "ruleengine:active_alarms";
    private const string AlarmHashPrefix = "ruleengine:alarm:";

    private readonly ConnectionMultiplexer _redis;
    private readonly IDatabase _db;
    private readonly ILogger<RedisAlarmWriter> _logger;

    public RedisAlarmWriter(string connectionString, ILogger<RedisAlarmWriter> logger)
    {
        _redis = ConnectionMultiplexer.Connect(connectionString);
        _db = _redis.GetDatabase();
        _logger = logger;
    }

    public bool IsConnected => _redis.IsConnected;

    public async Task WriteRealtimeAlarmAsync(AlarmSnapshot alarm)
    {
        var key = AlarmHashPrefix + alarm.MonitorId;
        var json = JsonSerializer.Serialize(alarm);

        await _db.HashSetAsync(key, new HashEntry[]
        {
            new("monitor_id", alarm.MonitorId),
            new("monitor_key", alarm.MonitorKey),
            new("monitor_name", alarm.MonitorName),
            new("status_key", alarm.StatusKey),
            new("status_name", alarm.StatusName ?? ""),
            new("value", alarm.Value),
            new("occur_time", new DateTimeOffset(alarm.OccurTime).ToUnixTimeMilliseconds()),
            new("config_version", new DateTimeOffset(alarm.ConfigVersion).ToUnixTimeMilliseconds()),
            new("worker_id", alarm.WorkerId),
            new("full_json", json),
        });

        await _db.KeyExpireAsync(key, TimeSpan.FromMinutes(5)); // TTL: 保活 5 分钟
        await _db.SetAddAsync(ActiveAlarmsSetKey, alarm.MonitorId);

        _logger.LogDebug("Redis 写入实时报警: {MonitorId} {StatusKey}", alarm.MonitorId, alarm.StatusKey);
    }

    public async Task ClearRealtimeAlarmAsync(string monitorId)
    {
        var key = AlarmHashPrefix + monitorId;
        await _db.KeyDeleteAsync(key);
        await _db.SetRemoveAsync(ActiveAlarmsSetKey, monitorId);

        _logger.LogDebug("Redis 清除实时报警: {MonitorId}", monitorId);
    }

    public Task WriteHistoryAlarmAsync(AlarmEvent alarmEvent)
    {
        // Redis 不负责历史存储
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<AlarmSnapshot>> GetActiveAlarmsAsync()
    {
        var ids = await _db.SetMembersAsync(ActiveAlarmsSetKey);
        var alarms = new List<AlarmSnapshot>(ids.Length);

        foreach (var id in ids)
        {
            var key = AlarmHashPrefix + id.ToString();
            var json = await _db.HashGetAsync(key, "full_json");
            if (!json.IsNull)
            {
                var alarm = JsonSerializer.Deserialize<AlarmSnapshot>(json.ToString());
                if (alarm != null) alarms.Add(alarm);
            }
        }

        return alarms;
    }

    public async Task<AlarmSnapshot?> GetAlarmAsync(string monitorId)
    {
        var key = AlarmHashPrefix + monitorId;
        var json = await _db.HashGetAsync(key, "full_json");
        if (json.IsNull) return null;

        return JsonSerializer.Deserialize<AlarmSnapshot>(json.ToString());
    }

    /// <summary>
    /// 从 Redis 批量恢复监视项的上一次状态。
    /// 1. SMEMBERS 获取活跃报警 ID 集合 (Set, O(N) 成员数)
    /// 2. 请求的 ID 在 Set 中 → Pipeline HGET status_key → 返回状态键
    /// 3. 请求的 ID 不在 Set 中 → PreviousStatus = "" (正常态)
    /// </summary>
    public async Task<Dictionary<string, string?>> GetLastEventStatusesAsync(IEnumerable<string> monitorIds)
    {
        var idList = monitorIds.ToList();
        if (idList.Count == 0)
            return new Dictionary<string, string?>();

        // Step 1: 获取所有活跃报警 ID
        var activeIds = new HashSet<string>();
        try
        {
            var members = await _db.SetMembersAsync(ActiveAlarmsSetKey);
            foreach (var m in members)
                activeIds.Add(m.ToString());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Redis SMEMBERS 失败，回退到空状态: {Count} 个 monitor", idList.Count);
            return idList.ToDictionary(id => id, _ => (string?)"");
        }

        // Step 2: 收集请求 ID 中在活跃集合内的
        var activeRequested = idList.Where(id => activeIds.Contains(id)).ToList();

        if (activeRequested.Count == 0)
        {
            // 全部正常态，无需查 Redis
            return idList.ToDictionary(id => id, _ => (string?)"");
        }

        // Step 3: Pipeline 批量 HGET status_key (每批 1000)
        var result = new Dictionary<string, string?>();
        const int batchSize = 1000;

        for (int i = 0; i < activeRequested.Count; i += batchSize)
        {
            var batch = activeRequested.Skip(i).Take(batchSize).ToList();
            var batchObj = _db.CreateBatch();
            var tasks = batch.Select(id =>
                batchObj.HashGetAsync(AlarmHashPrefix + id, "status_key")).ToArray();
            batchObj.Execute();

            var results = await Task.WhenAll(tasks);
            for (int j = 0; j < batch.Count; j++)
                result[batch[j]] = results[j].IsNull ? "" : results[j].ToString();
        }

        // Step 4: 不在活跃集合中的监视项 → 正常态
        foreach (var id in idList)
        {
            if (!result.ContainsKey(id))
                result[id] = "";
        }

        _logger.LogInformation("Redis 状态恢复: 请求 {Requested}, 活跃 {Active}, 恢复 {Recovered}",
            idList.Count, activeIds.Count, activeRequested.Count);

        return result;
    }

    public void Dispose() => _redis?.Dispose();
}
