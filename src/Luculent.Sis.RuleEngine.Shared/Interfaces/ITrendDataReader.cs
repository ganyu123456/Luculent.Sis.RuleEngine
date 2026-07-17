namespace Luculent.Sis.RuleEngine.Shared.Interfaces;

public interface ITrendDataReader
{
    Task<IDictionary<string, double?>> ReadBatchAsync(IEnumerable<string> tagNames);
    bool IsConnected { get; }
}
