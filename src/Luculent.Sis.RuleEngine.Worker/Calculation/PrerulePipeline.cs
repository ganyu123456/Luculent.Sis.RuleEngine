using Luculent.Sis.RuleEngine.Shared.Interfaces;
using Luculent.Sis.RuleEngine.Shared.Models;
using Microsoft.Extensions.Logging;

namespace Luculent.Sis.RuleEngine.Worker.Calculation;

/// <summary>
/// 前置规则检查管道。对标 MonitorCenter 的 PrerulePipeline + MonitorItemPipeline.PreruleCheck。
/// 在规则计算前执行抑制条件检查：手动停止、关联启停监视项、数据源依赖。
/// </summary>
public class PrerulePipeline
{
    private readonly IAlarmWriter _alarmWriter;
    private readonly ILogger<PrerulePipeline> _logger;

    public PrerulePipeline(IAlarmWriter alarmWriter, ILogger<PrerulePipeline> logger)
    {
        _alarmWriter = alarmWriter;
        _logger = logger;
    }

    /// <summary>
    /// 对单个监视项执行所有前置规则检查。任一检查返回抑制即短路。
    /// </summary>
    public async Task<PreruleCheckResult> CheckAsync(MonitorConfig monitor)
    {
        var prerule = monitor.Prerule;
        if (!prerule.IsEnabled)
            return PreruleCheckResult.Pass();

        // ① ManualFlag 检查
        if (prerule.EnableManualFlagCheck)
        {
            var result = CheckManualFlag(monitor);
            if (result.ShouldSuppress)
            {
                _logger.LogTrace("Monitor {Id} suppressed by ManualFlag", monitor.Id);
                return result;
            }
        }

        // ② StopMonitor 关联启停检查
        if (prerule.EnableStopMonitorCheck)
        {
            var result = await CheckStopMonitorAsync(monitor);
            if (result.ShouldSuppress)
            {
                _logger.LogTrace("Monitor {Id} suppressed by StopMonitor {Key}", monitor.Id, monitor.StopMonitorKey);
                return result;
            }
        }

        // ③ SourceDependency 数据源依赖检查
        if (prerule.EnableSourceDependencyCheck)
        {
            var result = await CheckSourceDependencyAsync(monitor);
            if (result.ShouldSuppress)
            {
                _logger.LogTrace("Monitor {Id} suppressed by SourceDependency", monitor.Id);
                return result;
            }
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

        // 查找 StopMonitorKey 对应的监视项是否处于报警状态
        // StopMonitorKey 可能是 MonitorId 或 MonitorKey
        var stopAlarm = await _alarmWriter.GetAlarmAsync(monitor.StopMonitorKey);
        if (stopAlarm != null)
            return PreruleCheckResult.Suppress(
                $"StopMonitor '{monitor.StopMonitorKey}' is in alarm state '{stopAlarm.StatusKey}'",
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
                    $"SourceDependency '{source.RelatedId}' (Key={source.Key}) is in alarm",
                    clearAlarm: false);
        }

        return PreruleCheckResult.Pass();
    }
}
