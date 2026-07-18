using System.Collections.Concurrent;
using Luculent.Sis.RuleEngine.Shared.Interfaces;
using Luculent.Sis.RuleEngine.Shared.Models;
using Luculent.Sis.RuleEngine.Worker.Calculation;
using Microsoft.Extensions.Logging;

namespace Luculent.Sis.RuleEngine.Worker;

/// <summary>
/// Worker 主计算循环。BackgroundService 由 .NET Host 管理生命周期。
/// </summary>
public class WorkerCalculationService : BackgroundService
{
    private readonly ITrendDataReader _trendReader;
    private readonly IStateStore _stateStore;
    private readonly IAlarmWriter _alarmWriter;
    private readonly IRuleDispatcher _dispatcher;
    private readonly IPrerulePipeline _prerule;
    private readonly PreruleEvaluationService _preruleEval;
    private readonly ILogger<WorkerCalculationService> _logger;

    /// <summary>
    /// 分配给本 Worker 的监控项。Master 通过 gRPC 推送后更新。
    /// </summary>
    public ConcurrentDictionary<string, MonitorConfig> AssignedMonitors { get; } = new();

    /// <summary>
    /// Worker 标识，用于在报警中区分不同 Worker。
    /// </summary>
    public string WorkerId { get; set; } = Environment.MachineName;

    private volatile bool _stateRecovered;
    private long _lastStateRecoveryTimeMs;

    public WorkerCalculationService(
        ITrendDataReader trendReader,
        IStateStore stateStore,
        IAlarmWriter alarmWriter,
        IRuleDispatcher dispatcher,
        IPrerulePipeline prerule,
        PreruleEvaluationService preruleEval,
        ILogger<WorkerCalculationService> logger)
    {
        _trendReader = trendReader;
        _stateStore = stateStore;
        _alarmWriter = alarmWriter;
        _dispatcher = dispatcher;
        _prerule = prerule;
        _preruleEval = preruleEval;
        _logger = logger;
    }

    /// <summary>
    /// 执行单个计算周期。暴露为 public 供性能测试使用。
    /// </summary>
    public async Task<int> RunOneCycleAsync(CancellationToken ct = default)
    {
        if (AssignedMonitors.IsEmpty)
            return 0;

        var tagNames = new HashSet<string>(
            AssignedMonitors.Values
                .Select(m => m.TagName)
                .Where(t => !string.IsNullOrEmpty(t)));

        foreach (var m in AssignedMonitors.Values)
        {
            var ruleOpts = m.RuleOptions;
            if (ruleOpts?.RangeDurationRules != null)
                foreach (var r in ruleOpts.RangeDurationRules)
                {
                    if (!string.IsNullOrEmpty(r.LeftTagName) && !tagNames.Contains(r.LeftTagName))
                        tagNames.Add(r.LeftTagName);
                    if (!string.IsNullOrEmpty(r.RightTagName) && !tagNames.Contains(r.RightTagName))
                        tagNames.Add(r.RightTagName);
                }
            if (ruleOpts?.RangeFrequencyRules != null)
                foreach (var r in ruleOpts.RangeFrequencyRules)
                {
                    if (!string.IsNullOrEmpty(r.LeftTagName) && !tagNames.Contains(r.LeftTagName))
                        tagNames.Add(r.LeftTagName);
                    if (!string.IsNullOrEmpty(r.RightTagName) && !tagNames.Contains(r.RightTagName))
                        tagNames.Add(r.RightTagName);
                }
            if (!string.IsNullOrEmpty(ruleOpts?.WallTemperatureOpts?.TemperatureTag)
                && !tagNames.Contains(ruleOpts.WallTemperatureOpts.TemperatureTag))
                tagNames.Add(ruleOpts.WallTemperatureOpts.TemperatureTag);
            if (!string.IsNullOrEmpty(ruleOpts?.WallTemperatureOpts?.ReferenceTag)
                && !tagNames.Contains(ruleOpts.WallTemperatureOpts.ReferenceTag))
                tagNames.Add(ruleOpts.WallTemperatureOpts.ReferenceTag);
        }

        var values = await _trendReader.ReadBatchAsync(tagNames);
        var now = DateTime.UtcNow;

        var dueMonitors = AssignedMonitors.Values
            .Where(m => (now - m.LastCalculateTime).TotalSeconds >= m.RefreshIntervalSecond)
            .ToList();

        if (dueMonitors.Count == 0)
            return 0;

        // 批量预加载状态，避免 N 次独立 I/O
        var monitorIds = dueMonitors.Select(m => m.Id).ToList();
        var preloadedStates = await _stateStore.GetBatchAsync(monitorIds);

        // Worker 状态恢复：对 StateStore 中缺失的 monitor 从 ClickHouse 查询最后事件恢复 PreviousStatus。
        // 启动后首次恢复延迟 2 秒，之后每 30 秒重新检查一次（处理 Master 重分区导致的 monitor 变更）。
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var shouldRecover = !_stateRecovered
            || (_stateRecovered && nowMs - _lastStateRecoveryTimeMs > 30_000);
        if (shouldRecover)
        {
            var missingIds = monitorIds.Where(id => !preloadedStates.ContainsKey(id)).ToList();
            if (missingIds.Count > 0)
            {
                var recovered = await _alarmWriter.GetLastEventStatusesAsync(missingIds);
                foreach (var (id, statusKey) in recovered)
                {
                    preloadedStates[id] = new CalculationState
                    {
                        MonitorId = id,
                        PreviousStatus = statusKey ?? "",
                    };
                }

                if (recovered.Count > 0 && !_stateRecovered)
                    _logger.LogInformation("Worker 启动恢复: 从 ClickHouse 恢复了 {Count} 个 monitor 的状态", recovered.Count);
                else if (recovered.Count > 0)
                    _logger.LogInformation("Worker 状态恢复: 恢复了 {Count} 个新分配 monitor 的状态", recovered.Count);
            }

            _stateRecovered = true;
            _lastStateRecoveryTimeMs = nowMs;
        }

        var modifiedStates = new ConcurrentDictionary<string, CalculationState>();

        await Parallel.ForEachAsync(dueMonitors,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = ct },
            (m, innerCt) =>
            {
                preloadedStates.TryGetValue(m.Id, out var preloaded);
                return new ValueTask(ProcessMonitorAsync(m, values, now, preloaded, modifiedStates, innerCt));
            });

        // 批量保存修改后的状态
        if (!modifiedStates.IsEmpty)
        {
            var batch = new Dictionary<string, CalculationState>(modifiedStates);
            await _stateStore.SaveBatchAsync(batch);
        }

        return dueMonitors.Count;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("Worker 计算服务启动");

        // 前置规则评估循环（独立运行，10s 周期）
        _ = Task.Run(async () =>
        {
            await Task.Delay(3000, ct); // 等待初始配置到达
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await _preruleEval.EvaluateAllAsync(ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "前置规则评估循环异常");
                }

                await Task.Delay(10000, ct);
            }
        }, ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var processed = await RunOneCycleAsync(ct);
                if (processed == 0)
                    await Task.Delay(100, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Worker 计算循环异常");
            }

            await Task.Delay(100, ct);
        }

        _logger.LogInformation("Worker 计算服务停止");
    }

    private async Task ProcessMonitorAsync(
        MonitorConfig monitor,
        IDictionary<string, double?> values,
        DateTime now,
        CalculationState? preloadedState,
        ConcurrentDictionary<string, CalculationState> modifiedStates,
        CancellationToken ct)
    {
        try
        {
            // ① 前置规则检查 (对标 MonitorCenter PreruleCheck 阶段)
            var preruleResult = await _prerule.CheckAsync(monitor);
            if (preruleResult.ShouldSuppress)
            {
                if (preruleResult.ShouldClearAlarm)
                {
                    await _alarmWriter.ClearRealtimeAlarmAsync(monitor.Id);

                    var preruleState = preloadedState
                        ?? new CalculationState { MonitorId = monitor.Id };
                    if (!string.IsNullOrEmpty(preruleState.PreviousStatus))
                    {
                        var lastEventId = preruleState.PreviousEventId;
                        var lastEventName = string.IsNullOrEmpty(preruleState.PreviousStatus)
                            ? null : preruleState.PreviousStatus;
                        var nowMs = new DateTimeOffset(now).ToUnixTimeMilliseconds();

                        await _alarmWriter.WriteHistoryAlarmAsync(new AlarmEvent
                        {
                            MonitorId = monitor.Id,
                            MonitorKey = monitor.Key,
                            MonitorName = monitor.Name,
                            StatusKey = "",
                            OccurTime = now,
                            ConfigVersion = monitor.LastModificationTime,
                            WorkerId = WorkerId,
                            LastEventId = lastEventId,
                            LastEventName = lastEventName,
                        });

                        preruleState.PreviousEventOccurTimeMs = nowMs;
                        preruleState.PreviousEventId = $"{monitor.Id}_{nowMs}_trigger";
                        preruleState.PreviousStatus = "";
                        modifiedStates[monitor.Id] = preruleState;
                    }
                }

                monitor.LastCalculateTime = now;
                return;
            }

            // ② 规则计算 (对标 MonitorCenter RuleCalculate 阶段)
            var result = await _dispatcher.CalculateAsync(monitor, values, now);
            var state = preloadedState
                ?? new CalculationState { MonitorId = monitor.Id, PreviousStatus = "" };

            var newStatus = result.HasEvent ? (result.State ?? "") : "";

            var unit = monitor.MonitorSources
                .FirstOrDefault(s => s.Key == monitor.FocusSourceId)?.Unit
                ?? monitor.MonitorSources.FirstOrDefault()?.Unit;

            // 实时报警更新
            if (result.HasEvent)
            {
                var alarmStates = new List<string>();
                if (!string.IsNullOrEmpty(result.State))
                    alarmStates.Add(result.State);
                if (result.States.Count > 0)
                    alarmStates.AddRange(result.States);
                if (result.StatesDic.Count > 0)
                    alarmStates.AddRange(result.StatesDic.Keys);

                foreach (var stateKey in alarmStates)
                {
                    if (string.IsNullOrEmpty(stateKey) || stateKey == "PACKAGECOMPLETEEVENT")
                        continue;

                    await _alarmWriter.WriteRealtimeAlarmAsync(new AlarmSnapshot
                    {
                        MonitorId = monitor.Id,
                        MonitorKey = monitor.Key,
                        MonitorName = monitor.Name,
                        StatusKey = stateKey,
                        StatusName = stateKey,
                        Value = result.TriggerValue ?? 0,
                        OccurTime = now,
                        ConfigVersion = monitor.LastModificationTime,
                        WorkerId = WorkerId,
                    });
                }
            }
            else
            {
                await _alarmWriter.ClearRealtimeAlarmAsync(monitor.Id);
            }

            // 状态变更流: newStatus != PreviousStatus → 写入事件
            if (newStatus != state.PreviousStatus)
            {
                var lastEventId = state.PreviousEventId;
                var lastEventName = string.IsNullOrEmpty(state.PreviousStatus)
                    ? null : state.PreviousStatus;

                await _alarmWriter.WriteHistoryAlarmAsync(new AlarmEvent
                {
                    MonitorId = monitor.Id,
                    MonitorKey = monitor.Key,
                    MonitorName = monitor.Name,
                    StatusKey = newStatus,
                    OccurTime = now,
                    TriggerValue = result.TriggerValue ?? 0,
                    ConfigVersion = monitor.LastModificationTime,
                    WorkerId = WorkerId,
                    Unit = unit,
                    JobId = $"{WorkerId}_{monitor.Key}_{now:yyyyMMddHHmmss}",
                    LastEventId = lastEventId,
                    LastEventName = lastEventName,
                });

                var nowMs = new DateTimeOffset(now).ToUnixTimeMilliseconds();
                state.PreviousEventOccurTimeMs = nowMs;
                state.PreviousEventId = $"{monitor.Id}_{nowMs}_trigger";
                state.PreviousStatus = newStatus;
                modifiedStates[monitor.Id] = state;
            }

            monitor.LastCalculateTime = now;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "处理监控项 {MonitorId} 异常", monitor.Id);
        }
    }
}
