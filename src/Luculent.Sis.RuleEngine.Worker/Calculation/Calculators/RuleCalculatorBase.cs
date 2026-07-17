using Luculent.Sis.RuleEngine.Shared.Enums;
using Luculent.Sis.RuleEngine.Shared.Models;
using Microsoft.Extensions.Logging;

namespace Luculent.Sis.RuleEngine.Worker.Calculation.Calculators;

/// <summary>
/// 纯计算基类。不依赖 MonitorCenter 的任何基类，无 IMonitorSourceStore、无 ABP。
/// </summary>
public abstract class RuleCalculatorBase
{
    protected readonly ILogger Logger;

    protected RuleCalculatorBase(ILogger logger)
    {
        Logger = logger;
    }

    protected static bool CompareSymbol(double left, double right, SymbolType symbol)
    {
        return symbol switch
        {
            SymbolType.Greater => left > right,
            SymbolType.GreaterOrEqual => left >= right,
            SymbolType.Less => left < right,
            SymbolType.LessOrEqual => left <= right,
            _ => false,
        };
    }

    protected static double? GetValue(IDictionary<string, double?> data, string tagName)
    {
        if (string.IsNullOrEmpty(tagName)) return null;
        return data.TryGetValue(tagName, out var val) ? val : null;
    }
}
