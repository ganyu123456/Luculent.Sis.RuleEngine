using Luculent.Sis.RuleEngine.Shared.Enums;
using Luculent.Sis.RuleEngine.Shared.Interfaces;
using Luculent.Sis.RuleEngine.Shared.Models;
using Microsoft.Extensions.Logging;

namespace Luculent.Sis.RuleEngine.Worker.Calculation.Calculators;

/// <summary>
/// 接口监控规则计算器。对标 MonitorCenter 的 CalculateInterfaceMonitoring。
/// 使用滑动窗口分析标签值变化，分类为 Normal / CollectError / InterfaceError / ManualStop / ShutDown。
/// </summary>
public class CalculateInterfaceMonitoring : RuleCalculatorBase
{
    private readonly IStateStore _stateStore;

    public CalculateInterfaceMonitoring(ILogger<CalculateInterfaceMonitoring> logger, IStateStore stateStore)
        : base(logger)
    {
        _stateStore = stateStore;
    }

    public async Task<RuleCalculateResult> CalculateAsync(MonitorConfig monitor, IDictionary<string, double?> data, DateTime? calcTime = null)
    {
        var result = new RuleCalculateResult();
        var opts = monitor.RuleOptions?.InterfaceMonitoringOpts;
        if (opts == null) return RuleCalculateResult.Empty();

        var time = calcTime ?? DateTime.UtcNow;
        var failureCount = opts.FailureCount > 0 ? opts.FailureCount : monitor.FailureCount;

        // ManualFlag 检查
        if (monitor.ManualFlag == 0)
        {
            result.State = "ManualStop";
            result.InterfaceMonitorType = "ManualStop";
            result.HasEvent = true;
            return result;
        }

        var state = await _stateStore.GetAsync(monitor.Id);
        state ??= new CalculationState { MonitorId = monitor.Id, RuleType = RuleType.InterfaceMonitoring };
        state.InterfaceSamples ??= new Dictionary<string, List<TagSample>>();

        // 标签去重
        var distinctTags = data
            .Where(kv => kv.Value.HasValue)
            .Select(kv => kv.Key)
            .Distinct()
            .ToList();

        if (distinctTags.Count == 0)
        {
            result.State = "InterfaceError";
            result.InterfaceMonitorType = "InterfaceError";
            result.HasEvent = true;
            result.TriggerValue = -1;
            return result;
        }

        var limitCount = failureCount > 0 ? failureCount : 5;
        string? finalStatus = null;

        foreach (var tag in distinctTags)
        {
            var value = data[tag]!.Value;

            // 更新滑动窗口
            if (!state.InterfaceSamples.ContainsKey(tag))
                state.InterfaceSamples[tag] = new List<TagSample>();

            var samples = state.InterfaceSamples[tag];
            samples.Add(new TagSample { TagName = tag, Time = time, Value = value });

            // 保持窗口大小为 limitCount
            while (samples.Count > limitCount)
                samples.RemoveAt(0);

            if (samples.Count < limitCount)
            {
                // 数据不够，暂不判定
                finalStatus ??= "Normal";
                continue;
            }

            // 分析窗口内数据
            var values = samples.Select(s => s.Value).Distinct().ToList();
            var dates = samples.Select(s => s.Time).Distinct().ToList();
            var tagStatus = AnalyzeTag(values, dates);

            if (tagStatus == "Normal")
                finalStatus ??= "Normal";
            else if (tagStatus == "CollectError" && finalStatus != "Normal")
                finalStatus = "CollectError";
            else if (tagStatus == "InterfaceError" && finalStatus != "Normal" && finalStatus != "CollectError")
                finalStatus = "InterfaceError";
        }

        await _stateStore.SaveAsync(monitor.Id, state);

        finalStatus ??= "Normal";

        result.InterfaceMonitorType = finalStatus;
        result.EventValue = data.FirstOrDefault().Value;

        switch (finalStatus)
        {
            case "Normal":
                result.HasEvent = false;
                result.State = null;
                break;
            case "CollectError":
                result.State = "CollectError";
                result.HasEvent = true;
                break;
            case "InterfaceError":
                result.State = "InterfaceError";
                result.HasEvent = true;
                break;
            default:
                result.State = finalStatus;
                result.HasEvent = true;
                break;
        }

        return result;
    }

    private static string AnalyzeTag(List<double> values, List<DateTime> dates)
    {
        if (values.Count > 1)
            return "Normal"; // 值有变化 → 正常

        if (dates.Count > 1)
            return "CollectError"; // 值不变但时间变化 → 采集异常

        return "InterfaceError"; // 值和时间都不变 → 接口异常
    }
}
