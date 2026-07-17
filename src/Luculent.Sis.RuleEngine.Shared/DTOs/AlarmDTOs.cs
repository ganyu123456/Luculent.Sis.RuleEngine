namespace Luculent.Sis.RuleEngine.Shared.DTOs;

public class AlarmQueryRequest
{
    public List<string> MonitorIds { get; set; } = new();
    public DateTime? StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public List<string>? StatusKeys { get; set; }
    public List<string>? EventTypes { get; set; }
    public int SkipCount { get; set; }
    public int MaxResultCount { get; set; } = 100;
}

public class AlarmQueryResponse
{
    public List<AlarmEventDTO> Items { get; set; } = new();
    public long TotalCount { get; set; }
}

public class AlarmEventDTO
{
    public string MonitorId { get; set; } = string.Empty;
    public string MonitorKey { get; set; } = string.Empty;
    public string MonitorName { get; set; } = string.Empty;
    public string StatusKey { get; set; } = string.Empty;
    public string? StatusName { get; set; }
    public string EventType { get; set; } = string.Empty;
    public DateTime OccurTime { get; set; }
    public DateTime? ClearTime { get; set; }
    public double TriggerValue { get; set; }
    public string WorkerId { get; set; } = string.Empty;
}

public class RealtimeAlarmResponse
{
    public List<AlarmSnapshotDTO> Items { get; set; } = new();
}

public class AlarmSnapshotDTO
{
    public string MonitorId { get; set; } = string.Empty;
    public string MonitorKey { get; set; } = string.Empty;
    public string MonitorName { get; set; } = string.Empty;
    public string StatusKey { get; set; } = string.Empty;
    public string? StatusName { get; set; }
    public double Value { get; set; }
    public DateTime OccurTime { get; set; }
    public string WorkerId { get; set; } = string.Empty;
}

public class StatisticsRequest
{
    public List<string>? MonitorIds { get; set; }
    public DateTime? StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public string AggregateType { get; set; } = "Count";
}
