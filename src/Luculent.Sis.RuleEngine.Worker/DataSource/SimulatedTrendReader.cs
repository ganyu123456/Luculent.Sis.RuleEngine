using Luculent.Sis.RuleEngine.Shared.Interfaces;
using Microsoft.Extensions.Logging;

namespace Luculent.Sis.RuleEngine.Worker.DataSource;

/// <summary>
/// 模拟 TrendDB 数据读取器，用于开发/测试/生产模拟环境。
/// 基于正弦波生成周期性变化的测点值，模拟真实工业测点行为。
/// 每个测点根据其名称哈希获得独立的频率、相位和振幅，
/// 使得 left_val > right_val 等区间比较能持续触发和清除告警。
/// </summary>
public class SimulatedTrendReader : ITrendDataReader
{
    private readonly ILogger<SimulatedTrendReader> _logger;
    private readonly Random _random = new();
    private long _tick;

    /// <summary>正弦波基频周期（秒）</summary>
    private const double BasePeriodSeconds = 60.0;

    public bool IsConnected => true;

    public SimulatedTrendReader(ILogger<SimulatedTrendReader> logger)
    {
        _logger = logger;
    }

    public Task<IDictionary<string, double?>> ReadBatchAsync(IEnumerable<string> tagNames)
    {
        var tick = Interlocked.Increment(ref _tick);
        var elapsedSeconds = tick; // 每次调用视为 1 秒流逝
        var result = new Dictionary<string, double?>();

        foreach (var tag in tagNames)
        {
            result[tag] = SimulateTagValue(tag, elapsedSeconds);
        }

        _logger.LogTrace("模拟读取 {Count} 个测点 (tick={Tick})", result.Count, tick);
        return Task.FromResult<IDictionary<string, double?>>(result);
    }

    public Task<IDictionary<string, double?>> ReadHistoryBatchAsync(
        IEnumerable<string> tagNames, DateTime timestamp)
    {
        // 历史读取使用固定的 elapsed 偏移
        var result = new Dictionary<string, double?>();
        var seed = timestamp.Ticks / TimeSpan.TicksPerSecond;
        foreach (var tag in tagNames)
        {
            result[tag] = SimulateTagValue(tag, seed);
        }

        return Task.FromResult<IDictionary<string, double?>>(result);
    }

    /// <summary>
    /// 基于测点名称哈希生成正弦波值。
    /// 范围: 0 ~ 200，波形由基频 + 2 次谐波 + 噪声叠加。
    /// </summary>
    private static double SimulateTagValue(string tagName, double elapsedSeconds)
    {
        var hash = (uint)tagName.GetHashCode(StringComparison.Ordinal);

        // FeatureValue 标签生成离散整数 1/2/3，用于 TriggerValueDefDic 匹配
        if (tagName.StartsWith("feat_", StringComparison.Ordinal))
        {
            // 每 15 秒切换一次状态，不同 tag 有不同的相位偏移
            var cycleIndex = ((long)elapsedSeconds / 15 + hash % 3) % 3;
            return cycleIndex + 1; // 1, 2, or 3
        }

        // 从哈希派生的参数
        var phase = (hash % 360) * Math.PI / 180.0;          // 0 ~ 2π
        var periodFactor = 0.5 + (hash % 100) / 200.0;       // 0.5 ~ 1.0
        var amplitude = 40.0 + (hash % 120);                  // 40 ~ 160
        var baseOffset = 80.0 + (hash % 40);                  // 80 ~ 120

        var period = BasePeriodSeconds * periodFactor;
        var omega = 2.0 * Math.PI / period;

        // 基频 + 二次谐波 + 三次谐波
        var t = elapsedSeconds;
        var value = baseOffset
            + amplitude * 0.6 * Math.Sin(omega * t + phase)
            + amplitude * 0.25 * Math.Sin(2.0 * omega * t + phase * 1.7)
            + amplitude * 0.15 * Math.Sin(3.0 * omega * t + phase * 2.3);

        // 钳制到 0-200，模拟工业传感器量程
        return Math.Clamp(value, 0.0, 200.0);
    }
}
