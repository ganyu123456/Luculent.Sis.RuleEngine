using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Luculent.Sis.RuleEngine.Shared.Models;

namespace Luculent.Sis.RuleEngine.Worker.Storage;

/// <summary>
/// 前置规则状态缓存，对标 MonitorCenter PreruleStore 的分片 ConcurrentDictionary。
/// </summary>
public class PreruleStateStore
{
    private const int SHARD_COUNT = 64;
    private readonly ConcurrentDictionary<string, bool>[] _shards;
    private readonly ConcurrentDictionary<string, long>[] _timestamps;

    public PreruleStateStore()
    {
        _shards = new ConcurrentDictionary<string, bool>[SHARD_COUNT];
        _timestamps = new ConcurrentDictionary<string, long>[SHARD_COUNT];
        for (int i = 0; i < SHARD_COUNT; i++)
        {
            _shards[i] = new ConcurrentDictionary<string, bool>();
            _timestamps[i] = new ConcurrentDictionary<string, long>();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int GetShardIndex(string key)
    {
        uint hash = unchecked((uint)key.GetHashCode());
        return (int)(hash & (SHARD_COUNT - 1));
    }

    public void SetState(string preruleId, bool state)
        => _shards[GetShardIndex(preruleId)][preruleId] = state;

    public void SetState(string preruleId, bool state, long lastEvalTimeMs)
    {
        var idx = GetShardIndex(preruleId);
        _shards[idx][preruleId] = state;
        _timestamps[idx][preruleId] = lastEvalTimeMs;
    }

    public bool? GetState(string preruleId)
        => _shards[GetShardIndex(preruleId)].TryGetValue(preruleId, out var s) ? s : null;

    public (bool? State, long LastEvalTimeMs) GetStateWithTime(string preruleId)
    {
        var idx = GetShardIndex(preruleId);
        var state = _shards[idx].TryGetValue(preruleId, out var s) ? s : (bool?)null;
        var time = _timestamps[idx].TryGetValue(preruleId, out var t) ? t : 0L;
        return (state, time);
    }

    public IReadOnlyDictionary<string, bool> GetAllStates()
    {
        var result = new Dictionary<string, bool>();
        foreach (var shard in _shards)
            foreach (var kvp in shard)
                result[kvp.Key] = kvp.Value;
        return result;
    }

    public IReadOnlyDictionary<string, (bool State, long LastEvalTimeMs)> GetAllStatesWithTime()
    {
        var result = new Dictionary<string, (bool, long)>();
        for (int i = 0; i < SHARD_COUNT; i++)
        {
            foreach (var kvp in _shards[i])
            {
                var time = _timestamps[i].TryGetValue(kvp.Key, out var t) ? t : 0L;
                result[kvp.Key] = (kvp.Value, time);
            }
        }
        return result;
    }
}
