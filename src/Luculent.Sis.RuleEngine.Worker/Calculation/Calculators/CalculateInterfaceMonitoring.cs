using Luculent.Sis.RuleEngine.Shared.Models;
using Microsoft.Extensions.Logging;

namespace Luculent.Sis.RuleEngine.Worker.Calculation.Calculators;

/// <summary>
/// 接口监控规则计算器。
/// 对标 MonitorCenter 的 CalculateInterfaceMonitoring。
/// </summary>
public class CalculateInterfaceMonitoring : RuleCalculatorBase
{
    public CalculateInterfaceMonitoring(ILogger<CalculateInterfaceMonitoring> logger) : base(logger) { }

    public RuleCalculateResult Calculate(MonitorConfig monitor, IDictionary<string, double?> data, DateTime? calcTime = null)
    {
        var opts = monitor.RuleOptions?.InterfaceMonitoringOpts;
        if (opts == null) return RuleCalculateResult.Empty();

        // 接口监控: 检查测点值是否代表"不可达"状态
        var value = GetValue(data, monitor.TagName);
        if (value == null)
        {
            // 数据取不到本身可能就是一种异常
            return new RuleCalculateResult
            {
                State = opts.StatusKey,
                HasEvent = true,
                TriggerValue = -1,
            };
        }

        // 值为 0 表示接口不通
        if (value.Value <= 0)
        {
            return new RuleCalculateResult
            {
                State = opts.StatusKey,
                HasEvent = true,
                TriggerValue = value.Value,
            };
        }

        return RuleCalculateResult.Empty();
    }
}
