using Luculent.Sis.RuleEngine.Shared.Interfaces;
using Microsoft.Extensions.Logging;

namespace Luculent.Sis.RuleEngine.Worker.DataSource;

/// <summary>
/// 模拟 TrendDB 数据读取器，用于开发/测试环境。
/// 生产环境替换为真实的 TrendDB 连接池实现。
/// </summary>
public class SimulatedTrendReader : ITrendDataReader
{
    private readonly ILogger<SimulatedTrendReader> _logger;
    private readonly Random _random = new();
    private readonly Dictionary<string, double> _simulatedValues = new();

    public bool IsConnected => true;

    public SimulatedTrendReader(ILogger<SimulatedTrendReader> logger)
    {
        _logger = logger;
    }

    public Task<IDictionary<string, double?>> ReadBatchAsync(IEnumerable<string> tagNames)
    {
        var result = new Dictionary<string, double?>();

        foreach (var tag in tagNames)
        {
            if (!_simulatedValues.ContainsKey(tag))
                _simulatedValues[tag] = 50.0 + _random.NextDouble() * 100;

            var delta = (_random.NextDouble() - 0.5) * 10;
            _simulatedValues[tag] += delta;
            _simulatedValues[tag] = Math.Clamp(_simulatedValues[tag], 0, 200);
            result[tag] = _simulatedValues[tag];
        }

        _logger.LogTrace("模拟读取 {Count} 个测点", result.Count);
        return Task.FromResult<IDictionary<string, double?>>(result);
    }

    public Task<IDictionary<string, double?>> ReadHistoryBatchAsync(
        IEnumerable<string> tagNames, DateTime timestamp)
    {
        return ReadBatchAsync(tagNames);
    }
}
