using Luculent.Sis.RuleEngine.Shared.Models;

namespace Luculent.Sis.RuleEngine.Worker.Calculation;

public interface IRuleDispatcher
{
    Task<RuleCalculateResult> CalculateAsync(
        MonitorConfig monitor,
        IDictionary<string, double?> data,
        DateTime? calcTime = null);
}
