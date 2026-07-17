using Luculent.Sis.RuleEngine.Shared.Models;
using Microsoft.Extensions.Logging;

namespace Luculent.Sis.RuleEngine.Worker.Calculation.Calculators;

/// <summary>
/// 特征值规则计算器。
/// 对标 MonitorCenter 的 CalculateFeatureValue。
/// </summary>
public class CalculateFeatureValue : RuleCalculatorBase
{
    public CalculateFeatureValue(ILogger<CalculateFeatureValue> logger) : base(logger) { }

    public RuleCalculateResult Calculate(MonitorConfig monitor, IDictionary<string, double?> data)
    {
        var value = GetValue(data, monitor.TagName);
        if (value == null) return RuleCalculateResult.Empty();

        var opts = monitor.RuleOptions;
        if (opts == null) return RuleCalculateResult.Empty();

        // 特征值规则：基于 TagName 的值进行模式匹配
        // 根据现有代码，FeatureValue 通常通过配置的阈值判断
        if (opts.WallTemperatureOpts != null)
        {
            if (value.Value > opts.WallTemperatureOpts.Threshold)
            {
                return new RuleCalculateResult
                {
                    State = opts.WallTemperatureOpts.StatusKey,
                    HasEvent = true,
                    TriggerValue = value.Value,
                };
            }
        }

        return RuleCalculateResult.Empty();
    }
}
