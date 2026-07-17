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

            // 用词边界替换标签名为实际值（避免子串匹配问题）
            var expression = script;
            foreach (var (tag, value) in data)
            {
                if (!value.HasValue) continue;
                // 使用正则确保词边界匹配
                var pattern = $@"\b{Regex.Escape(tag)}\b";
                expression = Regex.Replace(expression, pattern,
                    value.Value.ToString(CultureInfo.InvariantCulture));
            }

            // 尝试用 DataTable 求布尔表达式
            try
            {
                var dt = new System.Data.DataTable();
                var evaluated = dt.Compute(expression, null);

                if (evaluated is bool boolResult && boolResult)
                {
                    // 使用配置的状态键（不直接使用表达式文本）
                    result.State = monitor.RuleOptions?.ExpressionStatusKey ?? "expression_triggered";
                    result.HasEvent = true;
                }
            }
            catch (System.Data.EvaluateException ex)
            {
                result.Logs.Add($"表达式计算失败: {expression} — {ex.Message}");
                result.IsSuccess = false;
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
}
