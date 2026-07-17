using System.Text.Json.Serialization;
using Luculent.Sis.RuleEngine.Shared.Enums;

namespace Luculent.Sis.RuleEngine.Shared.Models;

public class RuleCalculateResult
{
    public bool IsSuccess { get; set; } = true;
    public string? State { get; set; }

    /// <summary>多状态匹配结果 (PackageValue/RulePackageValue)</summary>
    public List<string> States { get; set; } = new();

    /// <summary>多状态带时间数据的匹配结果 (RulePackageValue)</summary>
    public Dictionary<string, EventData> StatesDic { get; set; } = new();

    /// <summary>多状态区间时长匹配结果 (MultiStateRangeDuration)</summary>
    public Dictionary<string, MultiStateEventData> MultiStateRangeDurationStatesDic { get; set; } = new();

    public List<string> Logs { get; set; } = new();
    public bool HasEvent { get; set; }
    public double? TriggerValue { get; set; }
    public SymbolType? TriggerSymbol { get; set; }

    /// <summary>接口监控类型结果</summary>
    public string? InterfaceMonitorType { get; set; }

    /// <summary>接口监控事件值</summary>
    public double? EventValue { get; set; }

    public static RuleCalculateResult Empty() => new() { IsSuccess = true };

    public static RuleCalculateResult Failed(string reason) => new()
    {
        IsSuccess = false,
        Logs = new List<string> { reason },
    };
}

/// <summary>事件数据</summary>
public class EventData
{
    public DateTime EventTime { get; set; }
    public double EventValue { get; set; }
}

/// <summary>多状态事件数据</summary>
public class MultiStateEventData
{
    public DateTime EventTime { get; set; }
    public double EventValue { get; set; }
    public string StatusKey { get; set; } = string.Empty;
}
