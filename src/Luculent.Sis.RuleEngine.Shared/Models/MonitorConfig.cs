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
    public MonitorRuleOptions RuleOptions { get; set; } = new();
    public DateTime LastModificationTime { get; set; } = DateTime.UtcNow;

    [JsonIgnore]
    public DateTime LastCalculateTime { get; set; } = DateTime.MinValue;
}
