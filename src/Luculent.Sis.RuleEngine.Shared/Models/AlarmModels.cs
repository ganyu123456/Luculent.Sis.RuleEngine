using System.Text.Json.Serialization;
using Luculent.Sis.RuleEngine.Shared.Enums;

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

    public AlarmEvent ToAlarmEvent() => new()
    {
        MonitorId = MonitorId,
        MonitorKey = MonitorKey,
        MonitorName = MonitorName,
        StatusKey = StatusKey,
        StatusName = StatusName,
        EventType = EventType.Trigger,
        OccurTime = OccurTime,
        TriggerValue = Value,
        ConfigVersion = ConfigVersion,
        WorkerId = WorkerId,
    };
}

public class AlarmEvent
{
    public string MonitorId { get; set; } = string.Empty;
    public string MonitorKey { get; set; } = string.Empty;
    public string MonitorName { get; set; } = string.Empty;
    public string StatusKey { get; set; } = string.Empty;
    public string? StatusName { get; set; }
    public EventType EventType { get; set; }
    public DateTime OccurTime { get; set; }
    public DateTime? ClearTime { get; set; }
    public double TriggerValue { get; set; }
    public double? ThresholdValue { get; set; }
    public DateTime ConfigVersion { get; set; }
    public string WorkerId { get; set; } = string.Empty;
}
