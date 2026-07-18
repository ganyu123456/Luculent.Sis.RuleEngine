using System.Collections.Concurrent;
using Luculent.Sis.RuleEngine.Shared.Models;

namespace Luculent.Sis.RuleEngine.Worker.Storage;

/// <summary>
/// 前置规则定义缓存，对标 MonitorCenter _preruleCacheItems。
/// </summary>
public class PreruleDefinitionStore
{
    private readonly ConcurrentDictionary<string, PreruleDefinition> _definitions = new();

    public void LoadAll(List<PreruleDefinition> definitions)
    {
        _definitions.Clear();
        foreach (var d in definitions)
            _definitions[d.Id] = d;
    }

    public PreruleDefinition? Get(string preruleId)
        => _definitions.TryGetValue(preruleId, out var d) ? d : null;

    public List<PreruleDefinition> GetAll()
        => _definitions.Values.ToList();

    public int Count => _definitions.Count;
}
