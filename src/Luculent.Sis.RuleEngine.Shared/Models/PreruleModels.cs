namespace Luculent.Sis.RuleEngine.Shared.Models;

/// <summary>前置规则检查结果</summary>
public class PreruleCheckResult
{
    /// <summary>是否应抑制本次计算</summary>
    public bool ShouldSuppress { get; set; }

    /// <summary>抑制原因（用于日志/调试）</summary>
    public string? SuppressReason { get; set; }

    /// <summary>抑制时是否应清除现有报警</summary>
    public bool ShouldClearAlarm { get; set; }

    public static PreruleCheckResult Pass() => new() { ShouldSuppress = false };
    public static PreruleCheckResult Suppress(string reason, bool clearAlarm = true) => new()
    {
        ShouldSuppress = true,
        SuppressReason = reason,
        ShouldClearAlarm = clearAlarm,
    };
}

/// <summary>前置规则配置（附加到 MonitorConfig）</summary>
public class PreruleConfig
{
    /// <summary>是否启用前置规则检查</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>手动标志检查: ManualFlag == 0 时抑制计算</summary>
    public bool EnableManualFlagCheck { get; set; } = true;

    /// <summary>启停监视项检查: StopMonitorKey 关联的监视项处于报警状态时抑制</summary>
    public bool EnableStopMonitorCheck { get; set; } = true;

    /// <summary>数据源依赖检查: MonitorSources 中相关监视项不可用时抑制</summary>
    public bool EnableSourceDependencyCheck { get; set; }
}
