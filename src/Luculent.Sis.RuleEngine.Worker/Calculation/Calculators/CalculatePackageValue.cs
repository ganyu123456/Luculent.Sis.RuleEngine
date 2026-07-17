using Luculent.Sis.RuleEngine.Shared.Models;
using Microsoft.Extensions.Logging;

namespace Luculent.Sis.RuleEngine.Worker.Calculation.Calculators;

/// <summary>
/// 打包值规则计算器（旧版）。
/// 对标 MonitorCenter 的 CalculatePackageValue。
/// </summary>
public class CalculatePackageValue : RuleCalculatorBase
{
    public CalculatePackageValue(ILogger<CalculatePackageValue> logger) : base(logger) { }

    public RuleCalculateResult Calculate(MonitorConfig monitor, IDictionary<string, double?> data)
    {
        var value = GetValue(data, monitor.TagName);
        if (value == null) return RuleCalculateResult.Empty();

        var rules = monitor.RuleOptions?.PackageValueRules?
            .Where(r => r.IsEnabled)
            .ToList();

        if (rules == null || rules.Count == 0)
            return RuleCalculateResult.Empty();

        var rawValue = (long)value.Value;

        foreach (var rule in rules)
        {
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
