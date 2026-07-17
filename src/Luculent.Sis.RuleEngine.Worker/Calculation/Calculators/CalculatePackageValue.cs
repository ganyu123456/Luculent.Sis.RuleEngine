using Luculent.Sis.RuleEngine.Shared.Models;
using Microsoft.Extensions.Logging;

namespace Luculent.Sis.RuleEngine.Worker.Calculation.Calculators;

/// <summary>
/// 打包值规则计算器（旧版单点打包）。对标 MonitorCenter 的 CalculatePackageValue。
/// 使用位与运算 ((1L &lt;&lt; kvp.Key) &amp; packValue) != 0 匹配 TriggerValueDefDic 中每个位。
/// 收集所有匹配的状态，无匹配时注入 PACKAGECOMPLETEEVENT。
/// </summary>
public class CalculatePackageValue : RuleCalculatorBase
{
    public CalculatePackageValue(ILogger<CalculatePackageValue> logger) : base(logger) { }

    public RuleCalculateResult Calculate(MonitorConfig monitor, IDictionary<string, double?> data)
    {
        var result = new RuleCalculateResult();

        if (monitor.MonitorStatusDefinitions.Count == 0)
            return RuleCalculateResult.Empty();

        // 验证所有状态定义都有触发值
        foreach (var def in monitor.MonitorStatusDefinitions)
        {
            if (def.TriggerValueDefDic.Count == 0 || def.TriggerValueDefDic.Values.All(string.IsNullOrEmpty))
                return RuleCalculateResult.Failed("MonitorStatusDefinitions 触发值为空");
        }

        var focusId = string.IsNullOrEmpty(monitor.FocusSourceId) ? monitor.TagName : monitor.FocusSourceId;
        var focusValue = GetValue(data, focusId);
        if (focusValue == null)
        {
            result.IsSuccess = false;
            result.Logs.Add($"FocusSourceId={focusId} 未找到数据");
            return result;
        }

        var packValue = (long)focusValue.Value;
        var now = DateTime.UtcNow;

        foreach (var def in monitor.MonitorStatusDefinitions)
        {
            foreach (var (key, statusKey) in def.TriggerValueDefDic)
            {
                if (string.IsNullOrEmpty(statusKey)) continue;

                // 位与运算: (1 << bitPosition) & packValue
                if (((1L << key) & packValue) != 0)
                {
                    result.States.Add(statusKey);
                    result.HasEvent = true;
                    result.TriggerValue = focusValue.Value;
                }
            }
        }

        // 无匹配时注入 PACKAGECOMPLETEEVENT（用于下游清空事件）
        if (result.States.Count == 0)
        {
            result.State = "PACKAGECOMPLETEEVENT";
            result.HasEvent = true;
        }

        return result;
    }
}
