using Luculent.Sis.RuleEngine.Shared.Enums;
using Luculent.Sis.RuleEngine.Shared.Interfaces;
using Luculent.Sis.RuleEngine.Shared.Models;
using Microsoft.Extensions.Logging;

namespace Luculent.Sis.RuleEngine.Worker.Calculation.Calculators;

/// <summary>
/// 多状态区间时长规则计算器。
/// 对标 MonitorCenter 的 CalculateRuleMultiStateRangeDuration。
/// </summary>
public class CalculateRuleMultiStateRangeDuration : RuleCalculatorBase
{
    private readonly IStateStore _stateStore;

    public CalculateRuleMultiStateRangeDuration(ILogger<CalculateRuleMultiStateRangeDuration> logger, IStateStore stateStore)
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

        var rules = monitor.RuleOptions?.MultiStateRules?
            .Where(r => r.IsEnabled)
            .ToList();

        if (rules == null || rules.Count == 0)
            return RuleCalculateResult.Empty();

        var state = await _stateStore.GetAsync(monitor.Id) ?? new CalculationState
        {
            MonitorId = monitor.Id,
            RuleType = RuleType.RuleMultiStateRangeDuration,
            MultiStateDurations = new Dictionary<string, double>(),
        };

        state.MultiStateDurations ??= new Dictionary<string, double>();

        foreach (var rule in rules)
        {
            var tagValue = GetValue(data, rule.TagName);
            if (tagValue == null) continue;

            foreach (var condition in rule.Conditions)
            {
                var isSatisfied = CompareSymbol(tagValue.Value, condition.RightValue, condition.SymbolType);

                if (isSatisfied)
                {
                    // 累计时长
                    if (!state.MultiStateDurations.ContainsKey(condition.StatusKey))
                        state.MultiStateDurations[condition.StatusKey] = 0;

                    state.MultiStateDurations[condition.StatusKey] += monitor.RefreshIntervalSecond;

                    if (state.MultiStateDurations[condition.StatusKey] >= condition.DurationSecond)
                    {
                        await _stateStore.SaveAsync(monitor.Id, state);
                        return new RuleCalculateResult
                        {
                            State = condition.StatusKey,
                            HasEvent = true,
                            TriggerValue = tagValue.Value,
                            TriggerSymbol = condition.SymbolType,
                        };
                    }
                }
                else
                {
                    // 条件不满足，重置该状态的累计时长
                    state.MultiStateDurations[condition.StatusKey] = 0;
                }
            }
        }

        await _stateStore.SaveAsync(monitor.Id, state);
        return RuleCalculateResult.Empty();
    }
}
