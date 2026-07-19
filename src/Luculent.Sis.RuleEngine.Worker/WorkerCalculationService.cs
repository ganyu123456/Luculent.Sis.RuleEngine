using System.Collections.Concurrent;
using Luculent.Sis.RuleEngine.Shared.Interfaces;
using Luculent.Sis.RuleEngine.Shared.Models;
using Luculent.Sis.RuleEngine.Worker.Calculation;
using Luculent.Sis.RuleEngine.Worker.DataAcquisition;
using Microsoft.Extensions.Logging;

namespace Luculent.Sis.RuleEngine.Worker;

/// <summary>
/// Worker 主计算服务。BackgroundService 由 .NET Host 管理生命周期。
///
/// 架构（模式 A）:
///   DataAcquisitionService (1s) → TagValueStore → WorkerCalculationService (1s)
///   采集与计算分离，计算只读缓存，不直接查 TrendDB。
/// </summary>
public class WorkerCalculationService : BackgroundService
{
    private readonly IStateStore _stateStore;
    private readonly IAlarmWriter _alarmWriter;
    private readonly IRuleDispatcher _dispatcher;
    private readonly IPrerulePipeline _prerule;
    private readonly PreruleEvaluationService _preruleEval;
    private readonly TagValueStore _tagValues;
    private readonly ILogger<WorkerCalculationService> _logger;
    private readonly SemaphoreSlim _cycleLock = new(1, 1);

    /// <summary>
    /// 分配给本 Worker 的监控项。Master 通过 gRPC 推送后更新。
    /// </summary>
    public ConcurrentDictionary<string, MonitorConfig> AssignedMonitors { get; } = new();

    public string WorkerId { get; set; } = Environment.MachineName;

    private volatile bool _stateRecovered;
    private long _lastStateRecoveryTimeMs;

    public WorkerCalculationService(
        IStateStore stateStore,
        IAlarmWriter alarmWriter,
        IRuleDispatcher dispatcher,
        IPrerulePipeline prerule,
        PreruleEvaluationService preruleEval,
        TagValueStore tagValues,
        ILogger<WorkerCalculationService> logger)
    {
        _stateStore = stateStore;
        _alarmWriter = alarmWriter;
        _dispatcher = dispatcher;
        _prerule = prerule;
        _preruleEval = preruleEval;
        _tagValues = tagValues;
        _logger = logger;
    }

    /// <summary>
    /// 收集所有监视项关联的 tag 名，供 DataAcquisitionService 批量采集。
    /// </summary>
    public HashSet<string> GetAllTagNames()
    {
        var tags = new HashSet<string>(
            AssignedMonitors.Values
                .Select(m => m.TagName)
                .Where(t => !string.IsNullOrEmpty(t)));

        foreach (var m in AssignedMonitors.Values)
        {
            var ruleOpts = m.RuleOptions;
            if (ruleOpts?.RangeDurationRules != null)
                foreach (var r in ruleOpts.RangeDurationRules)
                {
                    if (!string.IsNullOrEmpty(r.LeftTagName)) tags.Add(r.LeftTagName);
                    if (!string.IsNullOrEmpty(r.RightTagName)) tags.Add(r.RightTagName);
                }
            if (ruleOpts?.RangeFrequencyRules != null)
                foreach (var r in ruleOpts.RangeFrequencyRules)
                {
                    if (!string.IsNullOrEmpty(r.LeftTagName)) tags.Add(r.LeftTagName);
                    if (!string.IsNullOrEmpty(r.RightTagName)) tags.Add(r.RightTagName);
                }
            if (!string.IsNullOrEmpty(ruleOpts?.WallTemperatureOpts?.TemperatureTag))
                tags.Add(ruleOpts.WallTemperatureOpts.TemperatureTag);
            if (!string.IsNullOrEmpty(ruleOpts?.WallTemperatureOpts?.ReferenceTag))
                tags.Add(ruleOpts.WallTemperatureOpts.ReferenceTag);
        }

        return tags;
    }

    /// <summary>
    /// 执行单个计算周期（读取缓存值）。暴露为 public 供测试使用。
    /// </summary>
    public async Task<int> RunOneCycleAsync(CancellationToken ct = default)
    {
        if (AssignedMonitors.IsEmpty)
            return 0;

        var values = _tagValues.Values;
        var now = DateTime.UtcNow;

        var dueMonitors = AssignedMonitors.Values
            .Where(m => (now - m.LastCalculateTime).TotalSeconds >= m.RefreshIntervalSecond)
            .ToList();

        if (dueMonitors.Count == 0)
            return 0;

        var monitorIds = dueMonitors.Select(m => m.Id).ToList();
        var preloadedStates = await _stateStore.GetBatchAsync(monitorIds);

        // 状态恢复: 缺失的状态从 ClickHouse 查询最后事件恢复
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var shouldRecover = !_stateRecovered
            || nowMs - _lastStateRecoveryTimeMs > 30_000;
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
                        PreviousEventId = "recovered", // 标记为非首事件，确保 lastEventName 填充
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

        if (!modifiedStates.IsEmpty)
        {
            var batch = new Dictionary<string, CalculationState>(modifiedStates);
            await _stateStore.SaveBatchAsync(batch);
        }

        return dueMonitors.Count;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("Worker 计算服务启动 (1s 周期, 读缓存)");

        // 前置规则评估循环（独立运行，10s 周期）
        _ = Task.Run(async () =>
        {
            await Task.Delay(3000, ct);
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

        // 主计算循环：1s PeriodicTimer，读 TagValueStore 缓存，不查 TrendDB
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            // SemaphoreSlim 防重叠：上一周期未完成则跳过本周期
            if (!await _cycleLock.WaitAsync(0, ct))
                continue;

            try
            {
                await RunOneCycleAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Worker 计算循环异常");
            }
            finally
            {
                _cycleLock.Release();
            }
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
            // ① 前置规则检查
            var preruleResult = await _prerule.CheckAsync(monitor);
            if (preruleResult.ShouldSuppress)
            {
                if (preruleResult.ShouldClearAlarm)
                {
                    var preruleState = preloadedState
                        ?? new CalculationState { MonitorId = monitor.Id };
                    if (!string.IsNullOrEmpty(preruleState.PreviousStatus))
                    {
                        await _alarmWriter.ClearRealtimeAlarmAsync(monitor.Id);

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

            // ② 规则计算
            var result = await _dispatcher.CalculateAsync(monitor, values, now);
            var state = preloadedState
                ?? new CalculationState { MonitorId = monitor.Id, PreviousStatus = "" };

            var newStatus = result.HasEvent ? (result.State ?? "") : "";

            var unit = monitor.MonitorSources
                .FirstOrDefault(s => s.Key == monitor.FocusSourceId)?.Unit
                ?? monitor.MonitorSources.FirstOrDefault()?.Unit;

            // ③ 状态变更 → 写入实时报警 + 历史事件
            // 实时报警只在状态变化时写入，保留报警开始时间
            if (newStatus != state.PreviousStatus)
            {
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

                var lastEventId = state.PreviousEventId;
                var lastEventName = state.PreviousEventId != null
                    ? (string.IsNullOrEmpty(state.PreviousStatus) ? "normal" : state.PreviousStatus)
                    : null;

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
