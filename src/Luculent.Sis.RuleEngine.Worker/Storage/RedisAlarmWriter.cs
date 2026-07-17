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

    public void Dispose() => _redis?.Dispose();
}
