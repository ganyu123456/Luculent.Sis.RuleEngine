using Luculent.Sis.RuleEngine.Shared.Enums;
using Luculent.Sis.RuleEngine.Shared.Interfaces;
using Luculent.Sis.RuleEngine.Shared.Models;
using Microsoft.Extensions.Logging;

namespace Luculent.Sis.RuleEngine.Worker.Calculation.Calculators;

/// <summary>
/// 区间频率规则计算器。
/// 对标 MonitorCenter 的 CalculateRuleRangeFrequency。
/// </summary>
public class CalculateRuleRangeFrequency : RuleCalculatorBase
{
    private readonly IStateStore _stateStore;

    public CalculateRuleRangeFrequency(ILogger<CalculateRuleRangeFrequency> logger, IStateStore stateStore)
        : base(logger)
    {
        _stateStore = stateStore;
    }

    public async Task<RuleCalculateResult> CalculateAsync(
        MonitorConfig monitor,
        IDictionary<string, double?> data,
        DateTime? calcTime = null)
    {
        var time = calcTime ?? DateTime.UtcNow;
        var timeMs = new DateTimeOffset(time).ToUnixTimeMilliseconds();

        var rules = monitor.RuleOptions?.RangeFrequencyRules?
            .Where(r => r.IsEnabled)
            .OrderBy(r => r.Priority)
            .ToList();

        if (rules == null || rules.Count == 0)
            return RuleCalculateResult.Empty();

        var state = await _stateStore.GetAsync(monitor.Id) ?? new CalculationState
        {
            MonitorId = monitor.Id,
            RuleType = RuleType.RangeFrequency,
        };

        foreach (var rule in rules)
        {
            var left = GetValue(data, rule.LeftTagName);
            var right = GetValue(data, rule.RightTagName);

            if (left == null || right == null) continue;

            var isSatisfied = CompareSymbol(left.Value, right.Value, rule.SymbolType);
            if (!isSatisfied) continue;

            // 记录本次触发时间
            state.FrequencyWindow.Add(timeMs);

            // 清除窗口外的旧数据
            var windowStart = timeMs - rule.WindowSeconds * 1000L;
            state.FrequencyWindow.RemoveAll(t => t < windowStart);

            // 判断频率是否达到阈值
            if (state.FrequencyWindow.Count >= rule.FrequencyCount)
            {
                var result = new RuleCalculateResult
                {
                    State = rule.StatusKey,
                    HasEvent = true,
                    TriggerValue = left.Value,
                    TriggerSymbol = rule.SymbolType,
                };

                await _stateStore.SaveAsync(monitor.Id, state);
                return result;
            }
        }

        await _stateStore.SaveAsync(monitor.Id, state);
        return RuleCalculateResult.Empty();
    }
}
