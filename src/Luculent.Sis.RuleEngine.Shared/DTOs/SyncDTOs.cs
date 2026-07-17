using Luculent.Sis.RuleEngine.Shared.Models;

namespace Luculent.Sis.RuleEngine.Shared.DTOs;

public class SyncFullRequest
{
    public List<MonitorConfig> Monitors { get; set; } = new();
    public string Version { get; set; } = string.Empty;
}

public class SyncDeltaRequest
{
    public List<MonitorConfig> Added { get; set; } = new();
    public List<MonitorConfig> Modified { get; set; } = new();
    public List<string> Deleted { get; set; } = new();
    public string Version { get; set; } = string.Empty;
}

public class SyncResponse
{
    public bool Success { get; set; }
    public int WorkerCount { get; set; }
    public int TotalMonitors { get; set; }
    public string? Error { get; set; }
}
