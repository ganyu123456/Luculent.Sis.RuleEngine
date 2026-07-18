namespace Luculent.Sis.RuleEngine.Shared.Models;

public class AlarmSnapshot
{
    public string MonitorId { get; set; } = string.Empty;
    public string MonitorKey { get; set; } = string.Empty;
    public string MonitorName { get; set; } = string.Empty;
    public string StatusKey { get; set; } = string.Empty;
    public string? StatusName { get; set; }
    public double Value { get; set; }
    public DateTime OccurTime { get; set; }
    public DateTime ConfigVersion { get; set; }
    public string WorkerId { get; set; } = string.Empty;
}

public class AlarmEvent
{
    public string MonitorId { get; set; } = string.Empty;
    public string MonitorKey { get; set; } = string.Empty;
    public string MonitorName { get; set; } = string.Empty;
    public string StatusKey { get; set; } = string.Empty;
    public string? StatusName { get; set; }
    public DateTime OccurTime { get; set; }
    public double TriggerValue { get; set; }
    public double? ThresholdValue { get; set; }
    public DateTime ConfigVersion { get; set; }
    public string WorkerId { get; set; } = string.Empty;

    public string? LastEventId { get; set; }
    public string? LastEventName { get; set; }
    public string? Unit { get; set; }
    public string? JobId { get; set; }
}
