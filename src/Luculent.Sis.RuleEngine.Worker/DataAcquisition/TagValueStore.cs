using System.Collections.Concurrent;

namespace Luculent.Sis.RuleEngine.Worker.DataAcquisition;

/// <summary>
/// 实时值缓存。DataAcquisitionService 写入，WorkerCalculationService 读取。
/// </summary>
public class TagValueStore
{
    private ConcurrentDictionary<string, double?> _values = new();

    /// <summary>当前缓存的所有 tag 值。</summary>
    public ConcurrentDictionary<string, double?> Values => _values;

    /// <summary>全量更新缓存。</summary>
    public void Update(IDictionary<string, double?> values)
    {
        foreach (var (k, v) in values)
            _values[k] = v;
    }
}
