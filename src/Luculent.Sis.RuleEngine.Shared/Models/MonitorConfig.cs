using System.Text.Json.Serialization;
using Luculent.Sis.RuleEngine.Shared.Enums;

namespace Luculent.Sis.RuleEngine.Shared.Models;

public class MonitorConfig
{
    public string Id { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public RuleType RuleType { get; set; }
    public int RefreshIntervalSecond { get; set; } = 60;
    public string TagName { get; set; } = string.Empty;

    /// <summary>FeatureValue/PackageValue 的主数据源键</summary>
    public string FocusSourceId { get; set; } = string.Empty;

    /// <summary>状态定义列表（含触发值字典）</summary>
    public List<MonitorStatusDefinition> MonitorStatusDefinitions { get; set; } = new();

    /// <summary>手动标志: 0=停止, 1=运行 (InterfaceMonitoring)</summary>
    public int? ManualFlag { get; set; }

    /// <summary>关联启停监视项 Key (InterfaceMonitoring)</summary>
    public string StopMonitorKey { get; set; } = string.Empty;

    /// <summary>故障判定计数阈值 (InterfaceMonitoring)</summary>
    public int? FailureCount { get; set; }

    /// <summary>监视项数据源定义列表</summary>
    public List<MonitorSourceDefinition> MonitorSources { get; set; } = new();

    public MonitorRuleOptions RuleOptions { get; set; } = new();
    public PreruleConfig Prerule { get; set; } = new();
    public DateTime LastModificationTime { get; set; } = DateTime.UtcNow;

    [JsonIgnore]
    public DateTime LastCalculateTime { get; set; } = DateTime.MinValue;
}

/// <summary>监视项状态定义</summary>
public class MonitorStatusDefinition
{
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string StatusKey { get; set; } = string.Empty;
    public double TriggerValue { get; set; }
    public double LevelValue { get; set; }
    public int DelayTime { get; set; }

    /// <summary>FeatureValue/PackageValue: 位键 -> 状态键的触发值映射</summary>
    public Dictionary<int, string> TriggerValueDefDic { get; set; } = new();
}

/// <summary>监视项数据源定义</summary>
public class MonitorSourceDefinition
{
    public string Key { get; set; } = string.Empty;
    public int SourceType { get; set; }
    public string RelatedId { get; set; } = string.Empty;
    public string Unit { get; set; } = string.Empty;
}
