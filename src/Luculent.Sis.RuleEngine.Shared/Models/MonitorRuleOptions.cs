using System.Text.Json.Serialization;
using Luculent.Sis.RuleEngine.Shared.Enums;

namespace Luculent.Sis.RuleEngine.Shared.Models;

public class MonitorRuleOptions
{
    public string? ExpressionScript { get; set; }
    public string? ExpressionStatusKey { get; set; }

    public List<RangeDurationRuleConfig> RangeDurationRules { get; set; } = new();
    public List<RangeFrequencyRuleConfig> RangeFrequencyRules { get; set; } = new();
    public List<PackageValueRuleConfig> PackageValueRules { get; set; } = new();
    public List<RulePackageValueRuleConfig> RulePackageValueRules { get; set; } = new();
    public List<MultiStateRangeDurationRuleConfig> MultiStateRules { get; set; } = new();
    public WallTemperatureOptions? WallTemperatureOpts { get; set; }
    public InterfaceMonitoringOptions? InterfaceMonitoringOpts { get; set; }
}

public class RangeDurationRuleConfig
{
    public string Id { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public string LeftTagName { get; set; } = string.Empty;
    public string RightTagName { get; set; } = string.Empty;
    public SymbolType SymbolType { get; set; }
    public string StatusKey { get; set; } = string.Empty;
    public int DurationSecond { get; set; }
    public bool BreakOnHit { get; set; }
    public int Priority { get; set; }
}

public class RangeFrequencyRuleConfig
{
    public string Id { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public string LeftTagName { get; set; } = string.Empty;
    public string RightTagName { get; set; } = string.Empty;
    public SymbolType SymbolType { get; set; }
    public string StatusKey { get; set; } = string.Empty;
    public int FrequencyCount { get; set; }
    public int WindowSeconds { get; set; }
    public bool BreakOnHit { get; set; }
    public int Priority { get; set; }
}

public class PackageValueRuleConfig
{
    public string Id { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public int BitPosition { get; set; }
    public int BitLength { get; set; }
    public string StatusKey { get; set; } = string.Empty;
    public int ExpectedValue { get; set; }
}

/// <summary>多打包点规则配置（对标 MonitorCenter RulePackageValue）</summary>
public class RulePackageValueRuleConfig
{
    public string Id { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public string SourceKey { get; set; } = string.Empty;
    public int StartKey { get; set; }
    public int EndKey { get; set; }
    public string StatusKey { get; set; } = string.Empty;
    public int Priority { get; set; }
    public bool BreakOnHit { get; set; }
}

public class MultiStateRangeDurationRuleConfig
{
    public string Id { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public string TagName { get; set; } = string.Empty;
    public List<MultiStateCondition> Conditions { get; set; } = new();
}

public class MultiStateCondition
{
    public string StatusKey { get; set; } = string.Empty;
    public double LeftValue { get; set; }
    public double RightValue { get; set; }
    public SymbolType SymbolType { get; set; }
    public int DurationSecond { get; set; }
}

public class WallTemperatureOptions
{
    public string TemperatureTag { get; set; } = string.Empty;
    public string ReferenceTag { get; set; } = string.Empty;
    public double Threshold { get; set; }
    public string StatusKey { get; set; } = string.Empty;
    public List<WallTemperatureLevelConfig> Levels { get; set; } = new();
}

public class WallTemperatureLevelConfig
{
    public string Id { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string StatusKey { get; set; } = string.Empty;
    public double LevelValue { get; set; }
    public int DelayTime { get; set; }
}

public class InterfaceMonitoringOptions
{
    public string Url { get; set; } = string.Empty;
    public int TimeoutSeconds { get; set; } = 30;
    public string StatusKey { get; set; } = string.Empty;
    public int FailureCount { get; set; } = 5;
    public int RefreshIntervalSecond { get; set; } = 60;
}
