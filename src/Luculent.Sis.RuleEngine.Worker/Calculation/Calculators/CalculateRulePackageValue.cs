using Luculent.Sis.RuleEngine.Shared.Models;
using Microsoft.Extensions.Logging;

namespace Luculent.Sis.RuleEngine.Worker.Calculation.Calculators;

/// <summary>
/// 多打包点规则计算器。对标 MonitorCenter 的 CalculateRulePackageValue。
/// 每个规则有 StartKey/EndKey 范围，在该范围内对 TriggerValueDefDic 执行位与运算。
/// 匹配的状态收集到 StatesDic，无匹配时注入 PACKAGECOMPLETEEVENT。
/// </summary>
public class CalculateRulePackageValue : RuleCalculatorBase
{
    public CalculateRulePackageValue(ILogger<CalculateRulePackageValue> logger) : base(logger) { }

    public RuleCalculateResult Calculate(MonitorConfig monitor, IDictionary<string, double?> data)
    {
        var result = new RuleCalculateResult();

        var rules = monitor.RuleOptions?.RulePackageValueRules?
            .Where(r => r.IsEnabled)
            .OrderBy(r => r.Priority)
            .ToList();

        if (rules == null || rules.Count == 0)
            return RuleCalculateResult.Empty();

        if (monitor.MonitorStatusDefinitions.Count == 0)
            return RuleCalculateResult.Empty();

        foreach (var def in monitor.MonitorStatusDefinitions)
        {
            if (def.TriggerValueDefDic.Count == 0 || def.TriggerValueDefDic.Values.All(string.IsNullOrEmpty))
                return RuleCalculateResult.Failed("MonitorStatusDefinitions 触发值为空");
        }

        var now = DateTime.UtcNow;

        foreach (var rule in rules)
        {
            // 获取打包值数据
            var sourceKey = string.IsNullOrEmpty(rule.SourceKey) ? monitor.TagName : rule.SourceKey;
            var packValueData = GetValue(data, sourceKey);
            if (packValueData == null) continue;

            var packValue = (long)packValueData.Value;

            // 在 TriggerValueDefDic 的 StartKey..EndKey 范围内执行位与运算
            foreach (var def in monitor.MonitorStatusDefinitions)
            {
                foreach (var (key, statusKey) in def.TriggerValueDefDic)
                {
                    if (string.IsNullOrEmpty(statusKey)) continue;
                    if (key < rule.StartKey || key > rule.EndKey) continue;

                    if (((1L << key) & packValue) != 0)
                    {
                        result.StatesDic[statusKey] = new EventData
                        {
                            EventTime = now,
                            EventValue = packValue,
                        };
                        result.HasEvent = true;
                        result.TriggerValue = packValue;
                    }
                }
            }

            if (rule.BreakOnHit && result.StatesDic.Count > 0)
                break;
        }

        if (result.StatesDic.Count == 0 || !result.HasEvent)
        {
            result.State = "PACKAGECOMPLETEEVENT";
            result.HasEvent = true;
        }

        return result;
    }
}
