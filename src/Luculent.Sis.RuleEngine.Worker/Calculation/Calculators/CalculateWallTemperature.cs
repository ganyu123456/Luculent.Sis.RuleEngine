using Luculent.Sis.RuleEngine.Shared.Enums;
using Luculent.Sis.RuleEngine.Shared.Interfaces;
using Luculent.Sis.RuleEngine.Shared.Models;
using Microsoft.Extensions.Logging;

namespace Luculent.Sis.RuleEngine.Worker.Calculation.Calculators;

/// <summary>
/// 壁温规则计算器。对标 MonitorCenter 的 CalculateWallTemperature。
/// 按 LevelValue 降序排列，带持续时间跟踪，通过 IStateStore 持久化状态。
/// </summary>
public class CalculateWallTemperature : RuleCalculatorBase
{
    private readonly IStateStore _stateStore;

    public CalculateWallTemperature(ILogger<CalculateWallTemperature> logger, IStateStore stateStore)
        : base(logger)
    {
        _stateStore = stateStore;
    }

    public async Task<RuleCalculateResult> CalculateAsync(MonitorConfig monitor, IDictionary<string, double?> data, DateTime? calcTime = null)
    {
        var result = new RuleCalculateResult();
        var opts = monitor.RuleOptions?.WallTemperatureOpts;
        if (opts == null) return RuleCalculateResult.Empty();

        var time = calcTime ?? DateTime.UtcNow;
        var timeMs = new DateTimeOffset(time).ToUnixTimeMilliseconds();

        var state = await _stateStore.GetAsync(monitor.Id);

        // 获取温度值（兼容旧版 "a" 别名）
        var tempTag = string.IsNullOrEmpty(opts.TemperatureTag) ? "a" : opts.TemperatureTag;
        var currentValue = GetValue(data, tempTag);

        if (currentValue == null)
        {
            if (state != null)
            {
                state.WallTemperatureStatusKey = null;
                state.WallTemperatureOccurTimeMs = 0;
                await _stateStore.SaveAsync(monitor.Id, state);
            }
            return result;
        }

        // 按 LevelValue 降序排列（最高优先级优先）
        var levels = opts.Levels?
            .OrderByDescending(l => l.LevelValue)
            .ToList() ?? new List<WallTemperatureLevelConfig>();

        // 如果未配置多级，使用简单阈值模式
        if (levels.Count == 0)
        {
            var refTag = opts.ReferenceTag;
            if (!string.IsNullOrEmpty(refTag))
            {
                var refValue = GetValue(data, refTag);
                if (refValue != null)
                {
                    var diff = Math.Abs(currentValue.Value - refValue.Value);
                    if (diff > opts.Threshold)
                    {
                        result.State = opts.StatusKey;
                        result.HasEvent = true;
                        result.TriggerValue = diff;
                    }
                }
            }
            else if (currentValue.Value > opts.Threshold)
            {
                result.State = opts.StatusKey;
                result.HasEvent = true;
                result.TriggerValue = currentValue.Value;
            }
            return result;
        }

        // 多级模式：按优先级检查每个级别
        foreach (var level in levels)
        {
            double compareValue;
            if (string.IsNullOrEmpty(level.Key) || !data.TryGetValue(level.Key, out var levelVal) || levelVal == null)
            {
                // 兼容旧版：用 LevelValue 直接比较
                compareValue = level.LevelValue;
            }
            else
            {
                compareValue = levelVal.Value;
            }

            var isHit = CompareSymbol(currentValue.Value, compareValue, SymbolType.GreaterOrEqual);
            if (!isHit) continue;

            if (level.DelayTime <= 0)
            {
                result.State = level.StatusKey;
                result.HasEvent = true;
                result.TriggerValue = currentValue.Value;
                return result;
            }

            // 带 DelayTime 的持续时间检查
            state ??= new CalculationState { MonitorId = monitor.Id, RuleType = RuleType.WallTemperatureValue };
            var durationHit = CheckWallTempDuration(level, timeMs, state);

            if (durationHit)
            {
                result.State = level.StatusKey;
                result.HasEvent = true;
                result.TriggerValue = currentValue.Value;
            }

            await _stateStore.SaveAsync(monitor.Id, state);
            return result;
        }

        // 所有级别都不满足，重置状态
        if (state != null)
        {
            state.WallTemperatureStatusKey = null;
            state.WallTemperatureOccurTimeMs = 0;
            await _stateStore.SaveAsync(monitor.Id, state);
        }

        return result;
    }

    private bool CheckWallTempDuration(WallTemperatureLevelConfig level, long timeMs, CalculationState state)
    {
        if (state.WallTemperatureStatusKey != level.StatusKey)
        {
            state.WallTemperatureStatusKey = level.StatusKey;
            state.WallTemperatureOccurTimeMs = timeMs;
            return false;
        }

        var elapsed = TimeSpan.FromMilliseconds(timeMs - state.WallTemperatureOccurTimeMs).TotalSeconds;
        return elapsed >= level.DelayTime;
    }
}
