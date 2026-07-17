using System.Text.Json.Serialization;
using Luculent.Sis.RuleEngine.Shared.Enums;

namespace Luculent.Sis.RuleEngine.Shared.Models;

public class CalculationState
{
    public string MonitorId { get; set; } = string.Empty;
    public RuleType RuleType { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long LastSatisfiedTimeMs { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double AccumulatedDurationSec { get; set; }

    public List<long> FrequencyWindow { get; set; } = new();

    public string? PreviousStatus { get; set; }

    public DateTime ConfigVersion { get; set; }

    /// <summary>多状态区间时长规则的子状态映射: statusKey → 累计时长(秒)</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, double>? MultiStateDurations { get; set; }

    /// <summary>壁温规则: 当前命中状态键</summary>
    public string? WallTemperatureStatusKey { get; set; }

    /// <summary>壁温规则: 开始满足条件的时间戳</summary>
    public long WallTemperatureOccurTimeMs { get; set; }

    /// <summary>接口监控: 每个标签的采样值队列</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, List<TagSample>>? InterfaceSamples { get; set; }

    /// <summary>接口监控: 关联启停监视项的标签缓存</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<TagSample>? RelatedMonitorTags { get; set; }
}

/// <summary>标签采样点</summary>
public class TagSample
{
    public string TagName { get; set; } = string.Empty;
    public DateTime Time { get; set; }
    public double Value { get; set; }
}
