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

    public PreruleStateStore()
    {
        _shards = new ConcurrentDictionary<string, bool>[SHARD_COUNT];
        for (int i = 0; i < SHARD_COUNT; i++)
            _shards[i] = new ConcurrentDictionary<string, bool>();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ConcurrentDictionary<string, bool> GetShard(string key)
    {
        uint hash = unchecked((uint)key.GetHashCode());
        return _shards[hash & (SHARD_COUNT - 1)];
    }

    public void SetState(string preruleId, bool state)
        => GetShard(preruleId)[preruleId] = state;

    public bool? GetState(string preruleId)
        => GetShard(preruleId).TryGetValue(preruleId, out var s) ? s : null;

    public IReadOnlyDictionary<string, bool> GetAllStates()
    {
        var result = new Dictionary<string, bool>();
        foreach (var shard in _shards)
            foreach (var kvp in shard)
                result[kvp.Key] = kvp.Value;
        return result;
    }
}
