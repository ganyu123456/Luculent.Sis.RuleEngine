using System.Globalization;
using System.Text.RegularExpressions;
using Luculent.Sis.RuleEngine.Shared.Interfaces;
using Luculent.Sis.RuleEngine.Shared.Models;
using Luculent.Sis.RuleEngine.Worker.DataAcquisition;
using Luculent.Sis.RuleEngine.Worker.Storage;
using Microsoft.Extensions.Logging;

namespace Luculent.Sis.RuleEngine.Worker.Calculation;

/// <summary>
/// 前置规则评估服务。以每个前置规则的 RefreshIntervalSecond 为周期，
/// 获取数据源、评估规则条件、缓存结果到 PreruleStateStore。
/// 对标 MonitorCenter PrerulePipeline (RuleCalculateBlock + PreruleCacheBlock)。
/// </summary>
public partial class PreruleEvaluationService
{
    private readonly PreruleDefinitionStore _defStore;
    private readonly PreruleStateStore _stateStore;
    private readonly TagValueStore _tagValues;
    private readonly ILogger<PreruleEvaluationService> _logger;

    public PreruleEvaluationService(
        PreruleDefinitionStore defStore,
        PreruleStateStore stateStore,
        TagValueStore tagValues,
        ILogger<PreruleEvaluationService> logger)
    {
        _defStore = defStore;
        _stateStore = stateStore;
        _tagValues = tagValues;
        _logger = logger;
    }

    /// <summary>评估全部前置规则，写入状态缓存</summary>
    public async Task EvaluateAllAsync(CancellationToken ct = default)
    {
        var definitions = _defStore.GetAll();
        if (definitions.Count == 0) return;

        _logger.LogInformation("前置规则评估开始: {Count} 条", definitions.Count);

        foreach (var def in definitions)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                bool state = await EvaluateOneAsync(def);
                var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                _stateStore.SetState(def.Id, state, nowMs);
                _logger.LogInformation("前置规则 {Id} = {State}", def.Id, state);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "前置规则 {Id} 评估失败", def.Id);
            }
        }
    }

    private async Task<bool> EvaluateOneAsync(PreruleDefinition def)
    {
        // 未启用 → 始终满足
        if (!def.IsEnabled)
            return true;

        // Step 1: 获取数据源当前值
        var sourceData = await FetchSourceDataAsync(def.MonitorSources);

        // Step 2: 评估规则条件
        bool hit = def.RuleType switch
        {
            1 => EvaluateExpression(def, sourceData),        // Expression
            2 => EvaluateRangeDuration(def, sourceData),     // RangeDuration
            _ => true,                                       // 未知类型默认满足
        };

        return hit;
    }

    private async Task<Dictionary<string, double>> FetchSourceDataAsync(
        List<PreruleSourceDefinition> sources)
    {
        var result = new Dictionary<string, double>();

        // Static (SourceType=1): SourceKey 就是常量值，直接 parse，不用查 TrendDB
        foreach (var s in sources.Where(s => s.SourceType == 1))
        {
            if (double.TryParse(s.SourceKey, NumberStyles.Float, CultureInfo.InvariantCulture, out var val))
            {
                if (!string.IsNullOrEmpty(s.Key))
                    result[s.Key] = val;
                if (!string.IsNullOrEmpty(s.SourceKey))
                    result[s.SourceKey] = val;
            }
        }

        // RealDB (SourceType=3): 从 TagValueStore 缓存读取，不直接查 TrendDB
        var cachedValues = _tagValues.Values;
        var realSources = sources.Where(s => s.SourceType == 3).ToList();
        if (realSources.Count > 0)
        {
            foreach (var rs in realSources)
            {
                if (string.IsNullOrEmpty(rs.SourceKey)) continue;

                if (cachedValues.TryGetValue(rs.SourceKey, out var val) && val.HasValue)
                {
                    result[rs.SourceKey] = val.Value;
                    if (!string.IsNullOrEmpty(rs.Key))
                        result[rs.Key] = val.Value;
                }
                else
                {
                    _logger.LogWarning("前置规则 RealDB 数据源 {TagName} 不在 TagValueStore 中", rs.SourceKey);
                }
            }
        }

        return result;
    }

    private static bool EvaluateExpression(PreruleDefinition def,
        Dictionary<string, double> sourceData)
    {
        if (def.RuleExpression == null || string.IsNullOrEmpty(def.RuleExpression.Code))
            return false;

        var code = def.RuleExpression.Code;
        // 替换数据源别名为实际值
        foreach (var (key, value) in sourceData)
        {
            code = code.Replace(key, value.ToString("G", CultureInfo.InvariantCulture));
        }

        try
        {
            return SafeEvaluate(code);
        }
        catch
        {
            return false;
        }
    }

    private static readonly Dictionary<string, double> _lastDurations = new();

    private bool EvaluateRangeDuration(PreruleDefinition def,
        Dictionary<string, double> sourceData)
    {
        if (def.RuleRangeDurations.Count == 0) return false;

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        bool anyHit = false;

        foreach (var rule in def.RuleRangeDurations.OrderBy(r => r.Priority))
        {
            if (!rule.IsEnabled) continue;

            sourceData.TryGetValue(rule.LeftSourceKey, out var leftVal);
            sourceData.TryGetValue(rule.RightSourceKey, out var rightVal);

            bool conditionMet = Compare(leftVal, rule.SymbolType, rightVal);

            var durationKey = $"{def.Id}_{rule.Id}";

            if (conditionMet)
            {
                if (!_lastDurations.ContainsKey(durationKey))
                    _lastDurations[durationKey] = now;

                var elapsed = (now - _lastDurations[durationKey]) / 1000.0;
                if (elapsed >= rule.DurationSecond)
                {
                    anyHit = true;
                    if (rule.BreakOnHit) return true;
                }
            }
            else
            {
                _lastDurations.Remove(durationKey);
            }
        }

        return anyHit;
    }

    private static bool Compare(double left, int symbolType, double right)
    {
        // SymbolType: 1=Greater, 2=GreaterOrEqual, 3=Less, 4=LessOrEqual
        return symbolType switch
        {
            1 => left > right,
            2 => left >= right,
            3 => left < right,
            4 => left <= right,
            _ => false,
        };
    }

    /// <summary>安全评估简单数学/逻辑表达式</summary>
    private static bool SafeEvaluate(string expr)
    {
        expr = expr.Trim();
        // 处理简单的比较表达式: a > b, a < b, a >= b, a <= b, a == b, a != b
        var match = ComparisonRegex().Match(expr);
        if (match.Success)
        {
            if (double.TryParse(match.Groups[1].Value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var left) &&
                double.TryParse(match.Groups[3].Value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var right))
            {
                return match.Groups[2].Value switch
                {
                    ">" => left > right,
                    ">=" => left >= right,
                    "<" => left < right,
                    "<=" => left <= right,
                    "==" => Math.Abs(left - right) < 0.0001,
                    "!=" => Math.Abs(left - right) >= 0.0001,
                    _ => false,
                };
            }
        }

        // 处理 && 和 || 组合
        if (expr.Contains("&&"))
        {
            var parts = expr.Split("&&", 2);
            return SafeEvaluate(parts[0]) && SafeEvaluate(parts[1]);
        }
        if (expr.Contains("||"))
        {
            var parts = expr.Split("||", 2);
            return SafeEvaluate(parts[0]) || SafeEvaluate(parts[1]);
        }

        // 尝试解析为纯数字（0=false, 非0=true）
        if (double.TryParse(expr, NumberStyles.Float, CultureInfo.InvariantCulture, out var num))
            return num != 0;

        return false;
    }

    [GeneratedRegex(@"^\s*([\d.-]+)\s*(>=|<=|!=|==|>|<)\s*([\d.-]+)\s*$")]
    private static partial Regex ComparisonRegex();
}
