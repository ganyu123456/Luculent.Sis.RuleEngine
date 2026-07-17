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
    private readonly ILogger<WorkerCalculationService> _logger;

    /// <summary>
    /// 分配给本 Worker 的监控项。Master 通过 gRPC 推送后更新。
    /// </summary>
    public ConcurrentDictionary<string, MonitorConfig> AssignedMonitors { get; } = new();

    public WorkerCalculationService(
        ITrendDataReader trendReader,
        IStateStore stateStore,
        IAlarmWriter alarmWriter,
        RuleDispatcher dispatcher,
        ILogger<WorkerCalculationService> logger)
    {
        _trendReader = trendReader;
        _stateStore = stateStore;
        _alarmWriter = alarmWriter;
        _dispatcher = dispatcher;
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

                // ① 收集所有需要读取的测点名称
                var tagNames = AssignedMonitors.Values
                    .Select(m => m.TagName)
                    .Distinct()
                    .ToList();

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
            var state = await _stateStore.GetAsync(monitor.Id);
            var result = await _dispatcher.CalculateAsync(monitor, values, state, now);

            if (result.HasEvent && !string.IsNullOrEmpty(result.State))
            {
                var alarm = new AlarmSnapshot
                {
                    MonitorId = monitor.Id,
                    MonitorKey = monitor.Key,
                    MonitorName = monitor.Name,
                    StatusKey = result.State,
                    StatusName = result.State,
                    Value = result.TriggerValue ?? 0,
                    OccurTime = now,
                    ConfigVersion = monitor.LastModificationTime,
                    WorkerId = Environment.MachineName,
                };

                await _alarmWriter.WriteRealtimeAlarmAsync(alarm);
                await _alarmWriter.WriteHistoryAlarmAsync(alarm.ToAlarmEvent());
            }
            else if (string.IsNullOrEmpty(result.State) && state?.PreviousStatus != null)
            {
                // 报警消除
                var clearEvent = new AlarmEvent
                {
                    MonitorId = monitor.Id,
                    MonitorKey = monitor.Key,
                    MonitorName = monitor.Name,
                    StatusKey = state.PreviousStatus,
                    EventType = Shared.Enums.EventType.Clear,
                    OccurTime = now,
                    ClearTime = now,
                    WorkerId = Environment.MachineName,
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
