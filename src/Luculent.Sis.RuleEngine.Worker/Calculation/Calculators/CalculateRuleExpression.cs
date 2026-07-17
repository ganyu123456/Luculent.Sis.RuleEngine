using Luculent.Sis.RuleEngine.Shared.Models;
using Microsoft.Extensions.Logging;

namespace Luculent.Sis.RuleEngine.Worker.Calculation.Calculators;

/// <summary>
/// 表达式规则计算器。
/// 对标 MonitorCenter 的 CalculateRuleExpression。
/// </summary>
public class CalculateRuleExpression : RuleCalculatorBase
{
    public CalculateRuleExpression(ILogger<CalculateRuleExpression> logger) : base(logger) { }

    public RuleCalculateResult Calculate(MonitorConfig monitor, IDictionary<string, double?> data)
    {
        var result = new RuleCalculateResult();

        try
        {
            var script = monitor.RuleOptions?.ExpressionScript;
            if (string.IsNullOrWhiteSpace(script))
            {
                return RuleCalculateResult.Empty();
            }

            // 将 data 字典中的值替换到表达式占位符中
            // 例如: "x > 100 and y < 50" → 用 data["x"] 和 data["y"] 替换
            var expression = script;
            foreach (var (tag, value) in data)
            {
                if (value.HasValue)
                {
                    expression = expression.Replace(tag, value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
            }

            // 使用 DataTable 或自定义表达式引擎求值
            // 此处简化: 假设表达式脚本返回的是状态键名字符串，如 "alarm" 或 "normal"
            try
            {
                var dt = new System.Data.DataTable();
                var evaluated = dt.Compute(expression, null);

                if (evaluated is bool boolResult && boolResult)
                {
                    result.State = monitor.RuleOptions?.ExpressionScript ?? "triggered";
                    result.HasEvent = true;
                }
            }
            catch (System.Data.EvaluateException)
            {
                result.Logs.Add($"表达式计算失败: {expression}");
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
