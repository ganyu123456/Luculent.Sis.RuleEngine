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

    /// <summary>
    /// 多状态区间时长规则的子状态映射: statusKey → 累计时长(秒)
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, double>? MultiStateDurations { get; set; }
}
