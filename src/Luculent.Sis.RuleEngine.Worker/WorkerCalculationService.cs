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
    private readonly RuleDispatcher _dispatcher;
    private readonly PrerulePipeline _prerule;
    private readonly ILogger<WorkerCalculationService> _logger;

    /// <summary>
    /// 分配给本 Worker 的监控项。Master 通过 gRPC 推送后更新。
    /// </summary>
    public ConcurrentDictionary<string, MonitorConfig> AssignedMonitors { get; } = new();

    /// <summary>
    /// Worker 标识，用于在报警中区分不同 Worker。
    /// </summary>
    public string WorkerId { get; set; } = Environment.MachineName;

    public WorkerCalculationService(
        ITrendDataReader trendReader,
        IStateStore stateStore,
        IAlarmWriter alarmWriter,
        RuleDispatcher dispatcher,
        PrerulePipeline prerule,
        ILogger<WorkerCalculationService> logger)
    {
        _trendReader = trendReader;
        _stateStore = stateStore;
        _alarmWriter = alarmWriter;
        _dispatcher = dispatcher;
        _prerule = prerule;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("Worker 计算服务启动");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (AssignedMonitors.IsEmpty)
                {
                    await Task.Delay(1000, ct);
                    continue;
                }

                // ① 收集所有需要读取的测点名称（MonitorConfig.TagName + 规则中的标签名）
                var tagNames = AssignedMonitors.Values
                    .Select(m => m.TagName)
                    .Where(t => !string.IsNullOrEmpty(t))
                    .ToList();

                // 收集规则中引用的所有标签名（RangeDuration / WallTemperature / etc.）
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

                // ② 从 TrendDB 批量读取实时值
                var values = await _trendReader.ReadBatchAsync(tagNames);
                var now = DateTime.UtcNow;

                // ③ 筛选本周期需要计算的监控项
                var dueMonitors = AssignedMonitors.Values
                    .Where(m => (now - m.LastCalculateTime).TotalSeconds >= m.RefreshIntervalSecond)
                    .ToList();

                if (dueMonitors.Count == 0)
                {
                    await Task.Delay(100, ct);
                    continue;
                }

                _logger.LogTrace("本周期计算 {Count} 个监控项", dueMonitors.Count);

                // ④ 并行计算
                var tasks = dueMonitors.Select(m => ProcessMonitorAsync(m, values, now, ct));
                await Task.WhenAll(tasks);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Worker 计算循环异常");
            }

            // 最小调度粒度
            await Task.Delay(100, ct);
        }

        _logger.LogInformation("Worker 计算服务停止");
    }

    private async Task ProcessMonitorAsync(
        MonitorConfig monitor,
        IDictionary<string, double?> values,
        DateTime now,
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

                    var preruleState = await _stateStore.GetAsync(monitor.Id);
                    if (preruleState?.PreviousStatus != null)
                    {
                        var clearEvent = new AlarmEvent
                        {
                            MonitorId = monitor.Id,
                            MonitorKey = monitor.Key,
                            MonitorName = monitor.Name,
                            StatusKey = preruleState.PreviousStatus,
                            EventType = Shared.Enums.EventType.Clear,
                            OccurTime = now,
                            ClearTime = now,
                            WorkerId = WorkerId,
                        };
                        await _alarmWriter.WriteHistoryAlarmAsync(clearEvent);
                    }
                }

                monitor.LastCalculateTime = now;
                return;
            }

            // ② 规则计算 (对标 MonitorCenter RuleCalculate 阶段)
            var result = await _dispatcher.CalculateAsync(monitor, values, now);
            var state = await _stateStore.GetAsync(monitor.Id);

            // 处理多状态结果 (PackageValue / RulePackageValue / MultiStateRangeDuration)
            if (result.HasEvent)
            {
                var alarmStates = new List<string>();

                if (!string.IsNullOrEmpty(result.State))
                    alarmStates.Add(result.State);

                if (result.States.Count > 0)
                    alarmStates.AddRange(result.States);

                if (result.StatesDic.Count > 0)
                    alarmStates.AddRange(result.StatesDic.Keys);

                var validStates = alarmStates.Where(s => !string.IsNullOrEmpty(s) && s != "PACKAGECOMPLETEEVENT").ToList();

                // 在写入实时报警前检查是否为新报警，以便正确写入历史事件
                var existingAlarm = await _alarmWriter.GetAlarmAsync(monitor.Id);
                bool isNewAlarm = validStates.Count > 0
                    && (existingAlarm == null || !validStates.Contains(existingAlarm.StatusKey));

                foreach (var stateKey in alarmStates)
                {
                    if (string.IsNullOrEmpty(stateKey) || stateKey == "PACKAGECOMPLETEEVENT")
                        continue;

                    var alarm = new AlarmSnapshot
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
                    };

                    await _alarmWriter.WriteRealtimeAlarmAsync(alarm);
                }

                // 仅在新报警时写入一条历史事件
                if (isNewAlarm)
                {
                    var firstState = validStates[0];
                    var historyAlarm = new AlarmSnapshot
                    {
                        MonitorId = monitor.Id,
                        MonitorKey = monitor.Key,
                        MonitorName = monitor.Name,
                        StatusKey = firstState,
                        StatusName = firstState,
                        Value = result.TriggerValue ?? 0,
                        OccurTime = now,
                        ConfigVersion = monitor.LastModificationTime,
                        WorkerId = WorkerId,
                    };
                    await _alarmWriter.WriteHistoryAlarmAsync(historyAlarm.ToAlarmEvent());
                }
            }
            else if (state?.PreviousStatus != null)
            {
                // 报警消除：先写 clear 事件，再清除实时报警
                var clearEvent = new AlarmEvent
                {
                    MonitorId = monitor.Id,
                    MonitorKey = monitor.Key,
                    MonitorName = monitor.Name,
                    StatusKey = state.PreviousStatus,
                    EventType = Shared.Enums.EventType.Clear,
                    OccurTime = now,
                    ClearTime = now,
                    WorkerId = WorkerId,
                };
                await _alarmWriter.WriteHistoryAlarmAsync(clearEvent);
                await _alarmWriter.ClearRealtimeAlarmAsync(monitor.Id);
            }

            monitor.LastCalculateTime = now;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "处理监控项 {MonitorId} 异常", monitor.Id);
        }
    }
}
