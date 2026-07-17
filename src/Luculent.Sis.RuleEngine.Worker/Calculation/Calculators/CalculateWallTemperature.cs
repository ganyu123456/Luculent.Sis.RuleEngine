using Luculent.Sis.RuleEngine.Shared.Models;
using Microsoft.Extensions.Logging;

namespace Luculent.Sis.RuleEngine.Worker.Calculation.Calculators;

/// <summary>
/// 壁温规则计算器。
/// 对标 MonitorCenter 的 CalculateWallTemperature。
/// </summary>
public class CalculateWallTemperature : RuleCalculatorBase
{
    public CalculateWallTemperature(ILogger<CalculateWallTemperature> logger) : base(logger) { }

    public RuleCalculateResult Calculate(MonitorConfig monitor, IDictionary<string, double?> data, DateTime? calcTime = null)
    {
        var opts = monitor.RuleOptions?.WallTemperatureOpts;
        if (opts == null) return RuleCalculateResult.Empty();

        var tempValue = GetValue(data, opts.TemperatureTag);
        var refValue = GetValue(data, opts.ReferenceTag);

        if (tempValue == null || refValue == null) return RuleCalculateResult.Empty();

        // 壁温计算：温差超过阈值则报警
        var difference = Math.Abs(tempValue.Value - refValue.Value);
        if (difference > opts.Threshold)
        {
            return new RuleCalculateResult
            {
                State = opts.StatusKey,
                HasEvent = true,
                TriggerValue = difference,
            };
        }

        return RuleCalculateResult.Empty();
    }
}
