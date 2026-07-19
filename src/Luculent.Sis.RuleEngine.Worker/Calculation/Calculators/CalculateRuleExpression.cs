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
    // 单例 Interpreter，表达式解析结果缓存于 DynamicExpresso 内部
    private static readonly Interpreter Interpreter = new(
        InterpreterOptions.DefaultCaseInsensitive);

    // 系统命名空间前缀 — 这些不是变量名
    private static readonly HashSet<string> SystemNamespaces = new(StringComparer.OrdinalIgnoreCase)
    {
        "Math", "WAS", "SISAI", "IM", "IC", "MathE", "SuShine",
    };

    // C# 关键字，不应被当作变量名处理
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
            // Step 1: 从表达式提取候选变量名（只处理实际出现的，避免 F1 全量遍历）
            var candidates = VariableRegex().Matches(script)
                .Select(m => m.Value)
                .Where(v => !IsSystemCall(v) && !ReservedWords.Contains(v))
                .Distinct()
                .ToList();

            // Step 2: 处理含点号的 tag 名 → 安全标识符
            var expr = script;
            var dottedMap = new Dictionary<string, string>(); // original → safeName
            var counter = 0;
            foreach (var name in candidates.Where(n => n.Contains('.')))
            {
                if (!data.ContainsKey(name)) continue;

                var safeName = $"__v{counter++}";
                dottedMap[name] = safeName;
                expr = expr.Replace(name, safeName);
            }

            // Step 3: 只设置表达式实际引用的变量
            var pars = new List<Parameter>(candidates.Count);
            foreach (var name in candidates)
            {
                if (!data.TryGetValue(name, out var val) || !val.HasValue)
                    continue;

                var paramName = dottedMap.TryGetValue(name, out var sn) ? sn : name;
                pars.Add(new Parameter(paramName, val.Value));
            }

            // Step 4: DynamicExpresso 求值（解析结果缓存，零 GC 压力，F2 消除）
            // 即使 pars 为空，常量表达式 (如 "100 > 50" 或 "true") 也应正常求值
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

    private static bool IsSystemCall(string identifier)
    {
        return SystemNamespaces.Any(ns =>
            identifier.StartsWith(ns + ".", StringComparison.OrdinalIgnoreCase));
    }

    [GeneratedRegex(@"[a-zA-Z_][\w.]*")]
    private static partial Regex VariableRegex();
}
