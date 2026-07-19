using System.Collections.Concurrent;
using System.Text.Json;
using Luculent.Sis.RuleEngine.Shared.Interfaces;
using Luculent.Sis.RuleEngine.Shared.Models;
using Luculent.Sis.RuleEngine.Worker.Calculation;
using Luculent.Sis.RuleEngine.Worker.DataAcquisition;
using Luculent.Sis.RuleEngine.Worker.Storage;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

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
    private readonly PreruleStateStore _preruleStateStore;
    private readonly ConnectionMultiplexer? _redis;
    private readonly SemaphoreSlim _cycleLock = new(1, 1);

    /// <summary>
    /// 分配给本 Worker 的监控项。Master 通过 gRPC 推送后更新。
    /// </summary>
    public ConcurrentDictionary<string, MonitorConfig> AssignedMonitors { get; } = new();

    public string WorkerId { get; set; } = Environment.MachineName;

    private volatile bool _stateRecovered;
    private long _lastStateRecoveryTimeMs;

    private HashSet<string>? _cachedTagNames;

    /// <summary>在 AssignedMonitors 变更后调用，使 tag 缓存失效。</summary>
    public void InvalidateTagNameCache() => _cachedTagNames = null;

    public WorkerCalculationService(
        IStateStore stateStore,
        IAlarmWriter alarmWriter,
        IRuleDispatcher dispatcher,
        IPrerulePipeline prerule,
        PreruleEvaluationService preruleEval,
        TagValueStore tagValues,
        PreruleStateStore preruleStateStore,
        ConnectionMultiplexer? redis,
        ILogger<WorkerCalculationService> logger)
    {
        _stateStore = stateStore;
        _alarmWriter = alarmWriter;
        _dispatcher = dispatcher;
        _prerule = prerule;
        _preruleEval = preruleEval;
        _tagValues = tagValues;
        _preruleStateStore = preruleStateStore;
        _redis = redis;
        _logger = logger;
    }

    /// <summary>
    /// 收集所有监视项关联的 tag 名，供 DataAcquisitionService 批量采集。
    /// </summary>
    public HashSet<string> GetAllTagNames()
    {
        if (_cachedTagNames != null)
            return _cachedTagNames;

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

        _cachedTagNames = tags;
        return tags;
    }

    /// <summary>
    /// 从当前采集数据中获取监视项的实际传感器值。
    /// </summary>
    private static double GetCurrentTagValue(MonitorConfig monitor, IDictionary<string, double?> values)
    {
        var ruleOpts = monitor.RuleOptions;
        if (ruleOpts?.RangeDurationRules != null)
        {
            foreach (var r in ruleOpts.RangeDurationRules)
            {
                if (!string.IsNullOrEmpty(r.LeftTagName)
                    && values.TryGetValue(r.LeftTagName, out var v)
                    && v.HasValue)
                    return v.Value;
            }
        }
        if (!string.IsNullOrEmpty(monitor.TagName)
            && values.TryGetValue(monitor.TagName, out var tagVal)
            && tagVal.HasValue)
            return tagVal.Value;

        return 0;
    }

    private record struct MonitorTransition(
        MonitorConfig Monitor,
        CalculationState State,
        string? NewStatus,
        List<string>? AlarmStates,
        double TriggerValue,
        string? Unit,
        string? JobId,
        string? LastEventId,
        string? LastEventName,
        double PrevMax,
        double PrevMin);

    /// <summary>
    /// 执行单个计算周期（读取缓存值）。暴露为 public 供测试使用。
    /// Phase 1: CPU 并行计算 | Phase 2: 批量 I/O 写入（F3 优化）
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
        var nowMsRecover = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var shouldRecover = !_stateRecovered
            || nowMsRecover - _lastStateRecoveryTimeMs > 30_000;
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
                        PreviousEventId = "recovered",
                    };
                }

                if (recovered.Count > 0 && !_stateRecovered)
                    _logger.LogInformation("Worker 启动恢复: 从 ClickHouse 恢复了 {Count} 个 monitor 的状态", recovered.Count);
                else if (recovered.Count > 0)
                    _logger.LogInformation("Worker 状态恢复: 恢复了 {Count} 个新分配 monitor 的状态", recovered.Count);
            }

            _stateRecovered = true;
            _lastStateRecoveryTimeMs = nowMsRecover;
        }

        // ===== Phase 1: CPU 并行计算（不写 I/O）=====
        var modifiedStates = new ConcurrentDictionary<string, CalculationState>();
        var transitions = new ConcurrentBag<MonitorTransition>();
        var nowMs = new DateTimeOffset(now).ToUnixTimeMilliseconds();

        await Parallel.ForEachAsync(dueMonitors,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = ct },
            (m, innerCt) =>
            {
                preloadedStates.TryGetValue(m.Id, out var preloaded);
                ComputeMonitor(m, values, now, preloaded, modifiedStates, transitions);
                return default;
            });

        // ===== Phase 2: 批量 I/O 写入 =====
        if (transitions.Count > 0)
        {
            var writeTasks = new List<Task>(transitions.Count * 3);
            foreach (var t in transitions)
            {
                if (!string.IsNullOrEmpty(t.NewStatus))
                {
                    foreach (var stateKey in t.AlarmStates ?? Enumerable.Empty<string>())
                    {
                        if (string.IsNullOrEmpty(stateKey) || stateKey == "PACKAGECOMPLETEEVENT")
                            continue;
                        writeTasks.Add(_alarmWriter.WriteRealtimeAlarmAsync(new AlarmSnapshot
                        {
                            MonitorId = t.Monitor.Id,
                            MonitorKey = t.Monitor.Key,
                            MonitorName = t.Monitor.Name,
                            StatusKey = stateKey,
                            StatusName = stateKey,
                            Value = t.TriggerValue,
                            OccurTime = now,
                            ConfigVersion = t.Monitor.LastModificationTime,
                            WorkerId = WorkerId,
                            RuleType = (int)t.Monitor.RuleType,
                        }));
                    }
                }
                else
                {
                    writeTasks.Add(_alarmWriter.ClearRealtimeAlarmAsync(t.Monitor.Id));
                }

                writeTasks.Add(_alarmWriter.WriteHistoryAlarmAsync(new AlarmEvent
                {
                    MonitorId = t.Monitor.Id,
                    MonitorKey = t.Monitor.Key,
                    MonitorName = t.Monitor.Name,
                    StatusKey = t.NewStatus ?? "",
                    StatusName = string.IsNullOrEmpty(t.NewStatus) ? null : t.NewStatus,
                    OccurTime = now,
                    TriggerValue = t.TriggerValue,
                    ConfigVersion = t.Monitor.LastModificationTime,
                    WorkerId = WorkerId,
                    Unit = t.Unit,
                    JobId = t.JobId,
                    LastEventId = t.LastEventId,
                    LastEventName = t.LastEventName,
                    MaxValue = t.PrevMax,
                    MinValue = t.PrevMin,
                    RuleType = (int)t.Monitor.RuleType,
                }));
            }

            await Task.WhenAll(writeTasks);
        }

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
                    await WritePreruleStatesToRedisAsync();
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

    private const string PreruleStatesHashKey = "ruleengine:prerule_states";

    private async Task WritePreruleStatesToRedisAsync()
    {
        if (_redis == null) return;

        try
        {
            var states = _preruleStateStore.GetAllStatesWithTime();
            var db = _redis.GetDatabase();
            var batch = new List<Task>();
            foreach (var (id, (state, timeMs)) in states)
            {
                var json = JsonSerializer.Serialize(
                    new { s = state, t = timeMs });
                batch.Add(db.HashSetAsync(PreruleStatesHashKey, id, json));
            }
            await Task.WhenAll(batch);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "写入前置规则状态到 Redis 失败");
        }
    }

    private void ComputeMonitor(
        MonitorConfig monitor,
        IDictionary<string, double?> values,
        DateTime now,
        CalculationState? preloadedState,
        ConcurrentDictionary<string, CalculationState> modifiedStates,
        ConcurrentBag<MonitorTransition> transitions)
    {
        try
        {
            // ① 前置规则检查
            var preruleResult = _prerule.CheckAsync(monitor).GetAwaiter().GetResult();
            if (preruleResult.ShouldSuppress)
            {
                if (preruleResult.ShouldClearAlarm)
                {
                    var preruleState = preloadedState
                        ?? new CalculationState { MonitorId = monitor.Id };
                    if (!string.IsNullOrEmpty(preruleState.PreviousStatus))
                    {
                        var lastEventId = preruleState.PreviousEventId;
                        var lastEventName = string.IsNullOrEmpty(preruleState.PreviousStatus)
                            ? null : preruleState.PreviousStatus;
                        var nowMs = new DateTimeOffset(now).ToUnixTimeMilliseconds();
                        var preruleUnit = monitor.MonitorSources
                            .FirstOrDefault(s => s.Key == monitor.FocusSourceId)?.Unit
                            ?? monitor.MonitorSources.FirstOrDefault()?.Unit;

                        transitions.Add(new MonitorTransition(
                            monitor,
                            preruleState,
                            "",
                            null,
                            GetCurrentTagValue(monitor, values),
                            preruleUnit,
                            $"{WorkerId}_{monitor.Key}_{now:yyyyMMddHHmmss}",
                            lastEventId,
                            lastEventName,
                            preruleState.MaxValue,
                            preruleState.MinValue));

                        preruleState.PreviousEventOccurTimeMs = nowMs;
                        preruleState.PreviousEventId = $"{monitor.Id}_{nowMs}_trigger";
                        preruleState.PreviousStatus = "";
                        preruleState.MaxValue = 0;
                        preruleState.MinValue = 0;
                        modifiedStates[monitor.Id] = preruleState;
                    }
                }

                monitor.LastCalculateTime = now;
                return;
            }

            // ② 规则计算
            var result = _dispatcher.CalculateAsync(monitor, values, now).GetAwaiter().GetResult();
            var state = preloadedState
                ?? new CalculationState { MonitorId = monitor.Id, PreviousStatus = "" };

            var newStatus = result.HasEvent ? (result.State ?? "") : "";

            var unit = monitor.MonitorSources
                .FirstOrDefault(s => s.Key == monitor.FocusSourceId)?.Unit
                ?? monitor.MonitorSources.FirstOrDefault()?.Unit;

            // ③ 状态变更 → 记录 transition（I/O 在 Phase 2 批量执行）
            if (newStatus != state.PreviousStatus)
            {
                var prevMax = state.MaxValue;
                var prevMin = state.MinValue;
                var currentValue = result.TriggerValue ?? 0;

                state.MaxValue = currentValue;
                state.MinValue = currentValue;

                var alarmStates = result.HasEvent
                    ? new List<string> { result.State ?? "" }
                        .Concat(result.States)
                        .Concat(result.StatesDic.Keys)
                        .Where(k => !string.IsNullOrEmpty(k) && k != "PACKAGECOMPLETEEVENT")
                        .Distinct()
                        .ToList()
                    : null;

                var lastEventId = state.PreviousEventId;
                var lastEventName = state.PreviousEventId != null
                    ? (string.IsNullOrEmpty(state.PreviousStatus) ? "" : state.PreviousStatus)
                    : null;

                var triggerValue = result.TriggerValue
                    ?? GetCurrentTagValue(monitor, values);

                transitions.Add(new MonitorTransition(
                    monitor,
                    state,
                    newStatus,
                    alarmStates,
                    triggerValue,
                    unit,
                    $"{WorkerId}_{monitor.Key}_{now:yyyyMMddHHmmss}",
                    lastEventId,
                    lastEventName,
                    prevMax,
                    prevMin));

                var nowMs = new DateTimeOffset(now).ToUnixTimeMilliseconds();
                state.PreviousEventOccurTimeMs = nowMs;
                state.PreviousEventId = $"{monitor.Id}_{nowMs}_trigger";
                state.PreviousStatus = newStatus;
                modifiedStates[monitor.Id] = state;
            }
            else
            {
                var currentValue = result.TriggerValue ?? 0;
                var changed = false;
                if (currentValue > state.MaxValue) { state.MaxValue = currentValue; changed = true; }
                if (currentValue < state.MinValue) { state.MinValue = currentValue; changed = true; }
                if (changed) modifiedStates[monitor.Id] = state;
            }

            monitor.LastCalculateTime = now;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "处理监控项 {MonitorId} 异常", monitor.Id);
        }
    }
}
