using Luculent.Sis.RuleEngine.Shared.Models;

namespace Luculent.Sis.RuleEngine.Shared.Interfaces;

public interface IStateStore
{
    Task<CalculationState?> GetAsync(string monitorId);
    Task SaveAsync(string monitorId, CalculationState state);
    Task DeleteAsync(string monitorId);
    Task<Dictionary<string, CalculationState>> GetBatchAsync(IEnumerable<string> monitorIds);
    Task SaveBatchAsync(Dictionary<string, CalculationState> states);
}
