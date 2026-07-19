using System.Globalization;
using System.Text.RegularExpressions;
using DynamicExpresso;
using Luculent.Sis.RuleEngine.Shared.Models;
using Microsoft.Extensions.Logging;

namespace Luculent.Sis.RuleEngine.Worker.Calculation.Calculators;

/// <summary>
/// 表达式规则计算器。对标 MonitorCenter 的 CalculateRuleExpression。
/// 基于 DynamicExpresso 引擎，支持 Math.* 函数族，表达式缓存，零分配变量替换。
/// </summary>
public partial class CalculateRuleExpression : RuleCalculatorBase
{
    private static readonly Interpreter Interpreter = new(
        InterpreterOptions.DefaultCaseInsensitive);

    private static readonly HashSet<string> SystemNamespaces = new(StringComparer.OrdinalIgnoreCase)
    {
        "Math", "WAS", "SISAI", "IM", "IC", "MathE", "SuShine",
    };

    private static readonly HashSet<string> ReservedWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "true", "false", "null",
    };

    public CalculateRuleExpression(ILogger<CalculateRuleExpression> logger) : base(logger) { }

    public RuleCalculateResult Calculate(MonitorConfig monitor, IDictionary<string, double?> data)
    {
        var script = monitor.RuleOptions?.ExpressionScript;
        if (string.IsNullOrWhiteSpace(script))
            return RuleCalculateResult.Empty();

        try
        {
            // Step 1: 从 MonitorSources 构建 source_key → 实际值 映射
            // data 字典的 key 是实际 tag 名 (source_cod)，表达式变量是 source_id (alias)
            var sourceMap = BuildSourceValueMap(monitor, data);

            // Step 2: 从表达式提取候选变量名
            var candidates = VariableRegex().Matches(script)
                .Select(m => m.Value)
                .Where(v => !IsSystemCall(v) && !ReservedWords.Contains(v))
                .Distinct()
                .ToList();

            // Step 3: 处理含点号的 tag 名 → 安全标识符
            var expr = script;
            var dottedMap = new Dictionary<string, string>();
            var counter = 0;
            foreach (var name in candidates)
            {
                if (!name.Contains('.'))
                    continue;

                // 先查 sourceMap (alias → value)，再查 data (直接 tag 名)
                if (!sourceMap.ContainsKey(name) && !data.ContainsKey(name))
                    continue;

                var safeName = $"__v{counter++}";
                dottedMap[name] = safeName;
                expr = expr.Replace(name, safeName);
            }

            // Step 4: 只设置表达式实际引用的变量
            var pars = new List<Parameter>(candidates.Count);
            foreach (var name in candidates)
            {
                var paramName = dottedMap.TryGetValue(name, out var sn) ? sn : name;

                // 优先从 sourceMap 取值 → data 字典作为 fallback
                if (sourceMap.TryGetValue(name, out var sv) && sv.HasValue)
                {
                    pars.Add(new Parameter(paramName, sv.Value));
                }
                else if (data.TryGetValue(name, out var dv) && dv.HasValue)
                {
                    pars.Add(new Parameter(paramName, dv.Value));
                }
            }

            // Step 5: DynamicExpresso 求值
            var evalResult = Interpreter.Eval<bool>(expr, pars.ToArray());

            if (evalResult)
            {
                return new RuleCalculateResult
                {
                    State = monitor.RuleOptions?.ExpressionStatusKey ?? "expression_triggered",
                    HasEvent = true,
                };
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "表达式规则计算异常 MonitorId={MonitorId}", monitor.Id);
            return new RuleCalculateResult { IsSuccess = false, Logs = { ex.Message } };
        }

        return RuleCalculateResult.Empty();
    }

    /// <summary>
    /// 从 MonitorSources 构建 source_id → 实际值 的映射。
    /// RealDB (SourceType=3): 通过 RelatedId (tag 名) 查 data 字典
    /// Static (SourceType=1): 直接解析 RelatedId 为数值
    /// </summary>
    private static Dictionary<string, double?> BuildSourceValueMap(
        MonitorConfig monitor, IDictionary<string, double?> data)
    {
        var map = new Dictionary<string, double?>();
        foreach (var src in monitor.MonitorSources)
        {
            if (string.IsNullOrEmpty(src.Key))
                continue;

            if (src.SourceType == 1) // Static value
            {
                if (double.TryParse(src.RelatedId, NumberStyles.Float,
                        CultureInfo.InvariantCulture, out var val))
                    map[src.Key] = val;
            }
            else if (src.SourceType == 3) // RealDB tag
            {
                if (data.TryGetValue(src.RelatedId, out var tv))
                    map[src.Key] = tv;
                else
                    map[src.Key] = null;
            }
            else // Other sources — try data directly
            {
                data.TryGetValue(src.RelatedId, out var ov);
                map[src.Key] = ov;
            }
        }

        return map;
    }

    private static bool IsSystemCall(string identifier)
    {
        return SystemNamespaces.Any(ns =>
            identifier.StartsWith(ns + ".", StringComparison.OrdinalIgnoreCase));
    }

    [GeneratedRegex(@"[a-zA-Z_][\w.]*")]
    private static partial Regex VariableRegex();
}
