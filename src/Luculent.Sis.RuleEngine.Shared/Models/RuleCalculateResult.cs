using System.Text.Json.Serialization;
using Luculent.Sis.RuleEngine.Shared.Enums;

namespace Luculent.Sis.RuleEngine.Shared.Models;

public class RuleCalculateResult
{
    public bool IsSuccess { get; set; } = true;
    public string? State { get; set; }
    public List<string> Logs { get; set; } = new();
    public bool HasEvent { get; set; }
    public double? TriggerValue { get; set; }
    public SymbolType? TriggerSymbol { get; set; }

    public static RuleCalculateResult Empty() => new() { IsSuccess = true };

    public static RuleCalculateResult Failed(string reason) => new()
    {
        IsSuccess = false,
        Logs = new List<string> { reason },
    };
}
