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

    public string? LastEventId { get; set; }
    public string? LastEventName { get; set; }
    public string? Unit { get; set; }
    public string? JobId { get; set; }

    /// <summary>关联的 trigger 事件发生时间，用于精确匹配 clear_time UPDATE</summary>
    public DateTime? RelatedTriggerOccurTime { get; set; }
}
