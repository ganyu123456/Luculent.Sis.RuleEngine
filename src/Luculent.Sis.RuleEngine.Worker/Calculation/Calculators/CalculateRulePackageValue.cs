using Luculent.Sis.RuleEngine.Shared.Models;
using Microsoft.Extensions.Logging;

namespace Luculent.Sis.RuleEngine.Worker.Calculation.Calculators;

/// <summary>
/// 打包值（位）规则计算器。
/// 对标 MonitorCenter 的 CalculateRulePackageValue。
/// </summary>
public class CalculateRulePackageValue : RuleCalculatorBase
{
    public CalculateRulePackageValue(ILogger<CalculateRulePackageValue> logger) : base(logger) { }

    public RuleCalculateResult Calculate(MonitorConfig monitor, IDictionary<string, double?> data)
    {
        var rules = monitor.RuleOptions?.PackageValueRules?
            .Where(r => r.IsEnabled)
            .ToList();

        if (rules == null || rules.Count == 0)
            return RuleCalculateResult.Empty();

        var value = GetValue(data, monitor.TagName);
        if (value == null) return RuleCalculateResult.Empty();

        var rawValue = (long)value.Value;

        foreach (var rule in rules)
        {
            // 提取指定位段的值
            var mask = ((1L << rule.BitLength) - 1) << rule.BitPosition;
            var extractedValue = (rawValue & mask) >> rule.BitPosition;

            if (extractedValue == rule.ExpectedValue)
            {
                return new RuleCalculateResult
                {
                    State = rule.StatusKey,
                    HasEvent = true,
                    TriggerValue = extractedValue,
                };
            }
        }

        return RuleCalculateResult.Empty();
    }
}
