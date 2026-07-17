using Luculent.Sis.RuleEngine.Shared.Enums;
using Luculent.Sis.RuleEngine.Shared.Models;
using Luculent.Sis.RuleEngine.Worker.Calculation.Calculators;
using Microsoft.Extensions.Logging;

namespace Luculent.Sis.RuleEngine.Worker.Calculation;

/// <summary>
/// 根据 RuleType 将监控项分发到对应的计算器。
/// 对标 MonitorCenter 的 RuleManager.Calculate switch 逻辑。
/// </summary>
public class RuleDispatcher
{
    private readonly CalculateRuleExpression _expressionCalc;
    private readonly CalculateRuleRangeDuration _rangeDurationCalc;
    private readonly CalculateRuleRangeFrequency _rangeFrequencyCalc;
    private readonly CalculateRulePackageValue _packageValueCalc;
    private readonly CalculateRuleMultiStateRangeDuration _multiStateCalc;
    private readonly CalculateFeatureValue _featureValueCalc;
    private readonly CalculatePackageValue _oldPackageValueCalc;
    private readonly CalculateWallTemperature _wallTempCalc;
    private readonly CalculateInterfaceMonitoring _interfaceCalc;
    private readonly ILogger<RuleDispatcher> _logger;

    public RuleDispatcher(
        CalculateRuleExpression expressionCalc,
        CalculateRuleRangeDuration rangeDurationCalc,
        CalculateRuleRangeFrequency rangeFrequencyCalc,
        CalculateRulePackageValue packageValueCalc,
        CalculateRuleMultiStateRangeDuration multiStateCalc,
        CalculateFeatureValue featureValueCalc,
        CalculatePackageValue oldPackageValueCalc,
        CalculateWallTemperature wallTempCalc,
        CalculateInterfaceMonitoring interfaceCalc,
        ILogger<RuleDispatcher> logger)
    {
        _expressionCalc = expressionCalc;
        _rangeDurationCalc = rangeDurationCalc;
        _rangeFrequencyCalc = rangeFrequencyCalc;
        _packageValueCalc = packageValueCalc;
        _multiStateCalc = multiStateCalc;
        _featureValueCalc = featureValueCalc;
        _oldPackageValueCalc = oldPackageValueCalc;
        _wallTempCalc = wallTempCalc;
        _interfaceCalc = interfaceCalc;
        _logger = logger;
    }

    public async Task<RuleCalculateResult> CalculateAsync(
        MonitorConfig monitor,
        IDictionary<string, double?> data,
        DateTime? calcTime = null)
    {
        try
        {
            return monitor.RuleType switch
            {
                RuleType.Expression => _expressionCalc.Calculate(monitor, data),
                RuleType.RangeDuration => await _rangeDurationCalc.CalculateAsync(monitor, data, calcTime),
                RuleType.RangeFrequency => await _rangeFrequencyCalc.CalculateAsync(monitor, data, calcTime),
                RuleType.PackageValue => _oldPackageValueCalc.Calculate(monitor, data),
                RuleType.FeatureValue => _featureValueCalc.Calculate(monitor, data),
                RuleType.WallTemperatureValue => await _wallTempCalc.CalculateAsync(monitor, data, calcTime),
                RuleType.InterfaceMonitoring => await _interfaceCalc.CalculateAsync(monitor, data, calcTime),
                RuleType.RulePackageValue => _packageValueCalc.Calculate(monitor, data),
                RuleType.RuleMultiStateRangeDuration => await _multiStateCalc.CalculateAsync(monitor, data, calcTime),
                _ => RuleCalculateResult.Empty(),
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "规则计算异常 MonitorId={MonitorId} RuleType={RuleType}", monitor.Id, monitor.RuleType);
            return RuleCalculateResult.Failed(ex.Message);
        }
    }
}
