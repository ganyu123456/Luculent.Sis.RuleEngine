using Luculent.Sis.RuleEngine.Shared.Models;

namespace Luculent.Sis.RuleEngine.Worker.Calculation;

public interface IPrerulePipeline
{
    Task<PreruleCheckResult> CheckAsync(MonitorConfig monitor);
}
