using Luculent.Sis.RuleEngine.Shared.Models;
using Microsoft.Extensions.Logging;

namespace Luculent.Sis.RuleEngine.Worker.Calculation.Calculators;

/// <summary>
/// 特征值规则计算器。对标 MonitorCenter 的 CalculateFeatureValue。
/// 通过 FocusSourceId 获取数据值，在 TriggerValueDefDic 中查找匹配的状态键。
/// </summary>
public class CalculateFeatureValue : RuleCalculatorBase
{
    public CalculateFeatureValue(ILogger<CalculateFeatureValue> logger) : base(logger) { }

    public RuleCalculateResult Calculate(MonitorConfig monitor, IDictionary<string, double?> data)
    {
        var result = new RuleCalculateResult();

        if (monitor.MonitorStatusDefinitions.Count == 0)
            return RuleCalculateResult.Empty();

        // 验证所有状态定义都有触发值
        foreach (var def in monitor.MonitorStatusDefinitions)
        {
            if (def.TriggerValueDefDic.Count == 0 || def.TriggerValueDefDic.Values.All(string.IsNullOrEmpty))
                return RuleCalculateResult.Failed($"MonitorStatusDefinitions 触发值为空: {def.Key}");
        }

        // 获取 FocusSourceId 对应的数据值
        var focusId = string.IsNullOrEmpty(monitor.FocusSourceId) ? monitor.TagName : monitor.FocusSourceId;
        var focusValue = GetValue(data, focusId);
        if (focusValue == null)
        {
            result.IsSuccess = false;
            result.Logs.Add($"FocusSourceId={focusId} 未找到数据");
            return result;
        }

        // 在 TriggerValueDefDic 中按整数值查找匹配的状态键
        var intValue = (int)focusValue.Value;
        foreach (var def in monitor.MonitorStatusDefinitions)
        {
            if (def.TriggerValueDefDic.TryGetValue(intValue, out var statusKey) && !string.IsNullOrEmpty(statusKey))
            {
                result.State = statusKey;
                result.HasEvent = true;
                result.TriggerValue = focusValue.Value;
                return result;
            }
        }

        // 未匹配任何状态键 — 不算错误，仅不触发
        Logger.LogWarning("FeatureValue 未匹配: MonitorId={MonitorId}, Value={Value}", monitor.Id, intValue);
        return result;
    }
}
