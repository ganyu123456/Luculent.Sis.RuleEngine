using Luculent.Sis.RuleEngine.Shared.Interfaces;
using Luculent.Sis.RuleEngine.Shared.Models;
using Luculent.Sis.RuleEngine.Worker.Storage;
using Microsoft.Extensions.Logging;

namespace Luculent.Sis.RuleEngine.Worker.Calculation;

/// <summary>
/// 前置规则检查管道：
/// ① PreruleId 检查 — 读取 PreruleStateStore 缓存，不满足则抑制
/// ② InterfaceMonitoring 抑制检查 — ManualFlag / StopMonitor / SourceDependency
/// </summary>
public class PrerulePipeline : IPrerulePipeline
{
    private readonly PreruleStateStore _preruleStateStore;
    private readonly IAlarmWriter _alarmWriter;
    private readonly ILogger<PrerulePipeline> _logger;

    public PrerulePipeline(
        PreruleStateStore preruleStateStore,
        IAlarmWriter alarmWriter,
        ILogger<PrerulePipeline> logger)
    {
        _preruleStateStore = preruleStateStore;
        _alarmWriter = alarmWriter;
        _logger = logger;
    }

    public async Task<PreruleCheckResult> CheckAsync(MonitorConfig monitor)
    {
        // ① 前置规则检查（对标 MonitorCenter PreruleCheckBlock）
        if (!string.IsNullOrEmpty(monitor.PreruleId))
        {
            var state = _preruleStateStore.GetState(monitor.PreruleId);
            if (state == null)
            {
                // 前置规则尚未就绪，跳过本轮
                _logger.LogTrace("Monitor {Id} prerule {PreruleId} not ready, skip", monitor.Id, monitor.PreruleId);
                return PreruleCheckResult.Suppress("Prerule not ready", clearAlarm: false);
            }

            if (!state.Value)
            {
                _logger.LogTrace("Monitor {Id} suppressed by prerule {PreruleId}", monitor.Id, monitor.PreruleId);
                return PreruleCheckResult.Suppress("不满足前置条件", clearAlarm: true);
            }
        }

        // ② InterfaceMonitoring 抑制检查
        var im = monitor.InterfaceMonitoring;
        if (!im.IsEnabled)
            return PreruleCheckResult.Pass();

        // ManualFlag 检查
        if (im.EnableManualFlagCheck)
        {
            var result = CheckManualFlag(monitor);
            if (result.ShouldSuppress) return result;
        }

        // StopMonitor 检查
        if (im.EnableStopMonitorCheck)
        {
            var result = await CheckStopMonitorAsync(monitor);
            if (result.ShouldSuppress) return result;
        }

        // SourceDependency 检查
        if (im.EnableSourceDependencyCheck)
        {
            var result = await CheckSourceDependencyAsync(monitor);
            if (result.ShouldSuppress) return result;
        }

        return PreruleCheckResult.Pass();
    }

    private static PreruleCheckResult CheckManualFlag(MonitorConfig monitor)
    {
        if (monitor.ManualFlag == 0)
            return PreruleCheckResult.Suppress("ManualFlag=0 (手动停止)", clearAlarm: true);
        return PreruleCheckResult.Pass();
    }

    private async Task<PreruleCheckResult> CheckStopMonitorAsync(MonitorConfig monitor)
    {
        if (string.IsNullOrEmpty(monitor.StopMonitorKey))
            return PreruleCheckResult.Pass();

        var stopAlarm = await _alarmWriter.GetAlarmAsync(monitor.StopMonitorKey);
        if (stopAlarm != null)
            return PreruleCheckResult.Suppress(
                $"StopMonitor '{monitor.StopMonitorKey}' is in alarm",
                clearAlarm: true);

        return PreruleCheckResult.Pass();
    }

    private async Task<PreruleCheckResult> CheckSourceDependencyAsync(MonitorConfig monitor)
    {
        if (monitor.MonitorSources.Count == 0)
            return PreruleCheckResult.Pass();

        foreach (var source in monitor.MonitorSources)
        {
            if (source.SourceType != 1 || string.IsNullOrEmpty(source.RelatedId))
                continue;

            var sourceAlarm = await _alarmWriter.GetAlarmAsync(source.RelatedId);
            if (sourceAlarm != null)
                return PreruleCheckResult.Suppress(
                    $"SourceDependency '{source.RelatedId}' is in alarm",
                    clearAlarm: false);
        }

        return PreruleCheckResult.Pass();
    }
}
