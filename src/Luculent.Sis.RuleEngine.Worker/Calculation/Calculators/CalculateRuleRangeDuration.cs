using Luculent.Sis.RuleEngine.Shared.Enums;
using Luculent.Sis.RuleEngine.Shared.Interfaces;
using Luculent.Sis.RuleEngine.Shared.Models;
using Microsoft.Extensions.Logging;

namespace Luculent.Sis.RuleEngine.Worker.Calculation.Calculators;

/// <summary>
/// 区间时长规则计算器。
/// 对标 MonitorCenter 的 CalculateRuleRangeDuration。
/// 状态存储改为通过 IStateStore 接口（RocksDB），不再使用静态 ConcurrentDictionary。
/// </summary>
public class CalculateRuleRangeDuration : RuleCalculatorBase
{
    private readonly IStateStore _stateStore;

    public CalculateRuleRangeDuration(ILogger<CalculateRuleRangeDuration> logger, IStateStore stateStore)
        : base(logger)
    {
        _stateStore = stateStore;
    }

    public async Task<RuleCalculateResult> CalculateAsync(
        MonitorConfig monitor,
        IDictionary<string, double?> data,
        DateTime? calcTime = null)
    {
        var result = new RuleCalculateResult();
        var time = calcTime ?? DateTime.UtcNow;
        var timeMs = new DateTimeOffset(time).ToUnixTimeMilliseconds();

        var rules = monitor.RuleOptions?.RangeDurationRules?
            .Where(r => r.IsEnabled)
            .OrderBy(r => r.Priority)
            .ToList();

        if (rules == null || rules.Count == 0)
        {
            return RuleCalculateResult.Empty();
        }

        // 确保 state 始终存在，从 CheckDurationHit 内部提到外部避免丢失
        var state = await _stateStore.GetAsync(monitor.Id)
                    ?? new CalculationState
                    {
                        MonitorId = monitor.Id,
                        RuleType = RuleType.RangeDuration,
                    };

        foreach (var rule in rules)
        {
            var left = GetValue(data, rule.LeftTagName);
            var right = GetValue(data, rule.RightTagName);

            if (left == null || right == null) continue;

            var isSatisfied = CompareSymbol(left.Value, right.Value, rule.SymbolType);

            if (rule.DurationSecond <= 0)
            {
                if (isSatisfied)
                {
                    result.State = rule.StatusKey;
                    result.HasEvent = true;
                    result.TriggerValue = left.Value;
                    result.TriggerSymbol = rule.SymbolType;
                    if (rule.BreakOnHit) break;
                }
                continue;
            }

            var durationHit = CheckDurationHit(rule, isSatisfied, timeMs, state);
            if (durationHit)
            {
                result.State = rule.StatusKey;
                result.HasEvent = true;
                result.TriggerValue = left.Value;
                result.TriggerSymbol = rule.SymbolType;
                if (rule.BreakOnHit) break;
            }
        }

        await _stateStore.SaveAsync(monitor.Id, state);
        return result;
    }

    private static bool CheckDurationHit(RangeDurationRuleConfig rule,
        bool hit, long timeMs, CalculationState state)
    {
        if (!hit)
        {
            state.LastSatisfiedTimeMs = 0;
            state.AccumulatedDurationSec = 0;
            return false;
        }

        if (state.PreviousStatus == rule.StatusKey && state.LastSatisfiedTimeMs > 0)
        {
            var elapsedSec = TimeSpan.FromMilliseconds(timeMs - state.LastSatisfiedTimeMs).TotalSeconds;
            if (elapsedSec >= rule.DurationSecond)
            {
                return true;
            }
        }
        else
        {
            state.LastSatisfiedTimeMs = timeMs;
            state.AccumulatedDurationSec = 0;
            state.PreviousStatus = rule.StatusKey;
        }

        return false;
    }
}
