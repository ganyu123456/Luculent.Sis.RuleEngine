namespace Luculent.Sis.RuleEngine.Shared.Interfaces;

public interface ITrendDataReader
{
    /// <summary>批量读取实时值。</summary>
    Task<IDictionary<string, double?>> ReadBatchAsync(IEnumerable<string> tagNames);

    /// <summary>批量读取指定时刻的历史值。</summary>
    Task<IDictionary<string, double?>> ReadHistoryBatchAsync(IEnumerable<string> tagNames, DateTime timestamp);

    bool IsConnected { get; }
}
