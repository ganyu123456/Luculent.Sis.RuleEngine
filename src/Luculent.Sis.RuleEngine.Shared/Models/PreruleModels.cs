namespace Luculent.Sis.RuleEngine.Shared.Models;

/// <summary>前置规则检查结果</summary>
public class PreruleCheckResult
{
    /// <summary>是否应抑制本次计算</summary>
    public bool ShouldSuppress { get; set; }

    /// <summary>抑制原因</summary>
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

/// <summary>InterfaceMonitoring 专属抑制配置（非前置规则系统）</summary>
public class InterfaceMonitoringConfig
{
    public bool IsEnabled { get; set; } = true;
    public bool EnableManualFlagCheck { get; set; } = true;
    public bool EnableStopMonitorCheck { get; set; } = true;
    public bool EnableSourceDependencyCheck { get; set; }
}

// ========== 前置规则定义 (对标 MonitorCenter PreruleCacheItem) ==========

/// <summary>前置规则定义，从 MonitorCenter 同步</summary>
public class PreruleDefinition
{
    /// <summary>前置规则主键</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>前置规则名称</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>描述</summary>
    public string Desc { get; set; } = string.Empty;

    /// <summary>刷新间隔(s)</summary>
    public int RefreshIntervalSecond { get; set; } = 60;

    /// <summary>关注数据源 ID</summary>
    public string FocusSourceId { get; set; } = string.Empty;

    /// <summary>启用状态</summary>
    public bool IsEnabled { get; set; }

    /// <summary>规则类型</summary>
    public int RuleType { get; set; }

    /// <summary>前置规则独立的数据源</summary>
    public List<PreruleSourceDefinition> MonitorSources { get; set; } = new();

    /// <summary>表达式规则条件</summary>
    public PreruleExpressionDefinition? RuleExpression { get; set; }

    /// <summary>区间时长规则条件</summary>
    public List<PreruleRangeDurationDefinition> RuleRangeDurations { get; set; } = new();

    public DateTime LastModificationTime { get; set; } = DateTime.UtcNow;
}

/// <summary>前置规则数据源 (对标 MonitorSourceCacheItem)</summary>
public class PreruleSourceDefinition
{
    public string Key { get; set; } = string.Empty;
    public int SourceType { get; set; }
    public string SourceKey { get; set; } = string.Empty;
    public string Unit { get; set; } = string.Empty;
}

/// <summary>表达式规则条件 (对标 RuleExpressionCacheItem)</summary>
public class PreruleExpressionDefinition
{
    public string Id { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string MonitorStatusId { get; set; } = string.Empty;
}

/// <summary>区间时长规则条件 (对标 RuleRangeDurationCacheItem)</summary>
public class PreruleRangeDurationDefinition
{
    public string Id { get; set; } = string.Empty;
    public string MonitorStatusKey { get; set; } = string.Empty;
    public string LeftSourceKey { get; set; } = string.Empty;
    public string RightSourceKey { get; set; } = string.Empty;
    public int SymbolType { get; set; }
    public int DurationSecond { get; set; }
    public bool IsEnabled { get; set; } = true;
    public int Priority { get; set; } = 1;
    public bool BreakOnHit { get; set; }
}
