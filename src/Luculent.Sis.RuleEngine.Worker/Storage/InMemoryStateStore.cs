using System.Collections.Concurrent;
using System.Text.Json;
using Luculent.Sis.RuleEngine.Shared.Interfaces;
using Luculent.Sis.RuleEngine.Shared.Models;

namespace Luculent.Sis.RuleEngine.Worker.Storage;

/// <summary>
/// 基于内存 ConcurrentDictionary 的状态存储实现。
/// 用于开发/测试环境，或不需要持久化的场景。
/// 生产环境应使用 RocksDbStateStore。
/// </summary>
public class InMemoryStateStore : IStateStore
{
    private readonly ConcurrentDictionary<string, CalculationState> _states = new();

    public Task<CalculationState?> GetAsync(string monitorId)
    {
        _states.TryGetValue(monitorId, out var state);
        return Task.FromResult(state);
    }

    public Task SaveAsync(string monitorId, CalculationState state)
    {
        _states[monitorId] = state;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string monitorId)
    {
        _states.TryRemove(monitorId, out _);
        return Task.CompletedTask;
    }

    public Task<Dictionary<string, CalculationState>> GetBatchAsync(IEnumerable<string> monitorIds)
    {
        var result = new Dictionary<string, CalculationState>();
        foreach (var id in monitorIds)
        {
            if (_states.TryGetValue(id, out var state) && state != null)
                result[id] = state;
        }
        return Task.FromResult(result);
    }

    public Task SaveBatchAsync(Dictionary<string, CalculationState> states)
    {
        foreach (var (id, state) in states)
            _states[id] = state;
        return Task.CompletedTask;
    }
}
