using System.Globalization;
using System.Text.RegularExpressions;
using Luculent.Sis.RuleEngine.Shared.Models;
using Microsoft.Extensions.Logging;

namespace Luculent.Sis.RuleEngine.Worker.Calculation.Calculators;

/// <summary>
/// 表达式规则计算器。对标 MonitorCenter 的 CalculateRuleExpression。
/// 支持变量替换和表达式求值，返回表达式结果对应的状态键。
/// </summary>
public partial class CalculateRuleExpression : RuleCalculatorBase
{
    public CalculateRuleExpression(ILogger<CalculateRuleExpression> logger) : base(logger) { }

    public RuleCalculateResult Calculate(MonitorConfig monitor, IDictionary<string, double?> data)
    {
        var result = new RuleCalculateResult();

        try
        {
            var script = monitor.RuleOptions?.ExpressionScript;
            if (string.IsNullOrWhiteSpace(script))
                return RuleCalculateResult.Empty();

            // F1 修复: 先解析表达式中的变量名，只替换实际出现的变量
            var expression = script;
            var usedVars = VariableRegex().Matches(script)
                .Select(m => m.Value)
                .Where(v => !IsKeyword(v))
                .Distinct()
                .ToList();

            foreach (var tag in usedVars)
            {
                if (data.TryGetValue(tag, out var val) && val.HasValue)
                    expression = expression.Replace(tag,
                        val.Value.ToString(CultureInfo.InvariantCulture));
            }

            // F2 修复: 用轻量解析替代 DataTable.Compute
            if (SafeEvaluateExpression(expression))
            {
                result.State = monitor.RuleOptions?.ExpressionStatusKey ?? "expression_triggered";
                result.HasEvent = true;
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "表达式规则计算异常 MonitorId={MonitorId}", monitor.Id);
            result.IsSuccess = false;
            result.Logs.Add(ex.Message);
        }

        return result;
    }

    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "and", "or", "not", "true", "false", "if", "iif",
    };

    private static bool IsKeyword(string s) => Keywords.Contains(s);

    [GeneratedRegex(@"[a-zA-Z_][\w.]*")]
    private static partial Regex VariableRegex();

    /// <summary>轻量布尔表达式求值，替代 DataTable.Compute。</summary>
    private static bool SafeEvaluateExpression(string expr)
    {
        expr = expr.Trim();

        // 处理 && 和 || 组合
        if (expr.Contains("&&"))
        {
            var parts = expr.Split("&&", 2);
            return SafeEvaluateExpression(parts[0]) && SafeEvaluateExpression(parts[1]);
        }
        if (expr.Contains("||"))
        {
            var parts = expr.Split("||", 2);
            return SafeEvaluateExpression(parts[0]) || SafeEvaluateExpression(parts[1]);
        }

        // 处理简单比较表达式: a > b, a < b, a >= b, a <= b, a == b, a != b
        var match = ComparisonRegex().Match(expr);
        if (!match.Success)
        {
            // 尝试解析为纯数字（0=false, 非0=true）
            if (double.TryParse(expr, NumberStyles.Float, CultureInfo.InvariantCulture, out var num))
                return num != 0;
            return false;
        }

        var leftStr = match.Groups[1].Value.Trim();
        var op = match.Groups[2].Value;
        var rightStr = match.Groups[3].Value.Trim();

        if (!double.TryParse(leftStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var left) ||
            !double.TryParse(rightStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var right))
            return false;

        return op switch
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

    [GeneratedRegex(@"^\s*([\d.-]+)\s*(>=|<=|!=|==|>|<)\s*([\d.-]+)\s*$")]
    private static partial Regex ComparisonRegex();
}
