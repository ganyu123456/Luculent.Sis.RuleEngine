using Luculent.Sis.RuleEngine.Shared.Enums;
using Luculent.Sis.RuleEngine.Shared.Models;
using Luculent.Sis.RuleEngine.Worker.Calculation.Calculators;
using Microsoft.Extensions.Logging;
using Moq;

namespace Luculent.Sis.RuleEngine.Tests.Calculation;

public class CalculateRuleExpression_Tests
{
    private readonly CalculateRuleExpression _calculator;

    public CalculateRuleExpression_Tests()
    {
        var logger = Mock.Of<ILogger<CalculateRuleExpression>>();
        _calculator = new CalculateRuleExpression(logger);
    }

    // ===== 基础功能 =====

    [Fact]
    public void Calculate_NoExpressionScript_ReturnsEmpty()
    {
        var monitor = new MonitorConfig
        {
            Id = "test-1",
            RuleType = RuleType.Expression,
            TagName = "temp",
        };
        var data = new Dictionary<string, double?> { ["temp"] = 100 };

        var result = _calculator.Calculate(monitor, data);

        Assert.True(result.IsSuccess);
        Assert.Null(result.State);
    }

    [Fact]
    public void Calculate_ConstantTrue_TriggersExpression()
    {
        var monitor = new MonitorConfig
        {
            Id = "test-2",
            RuleType = RuleType.Expression,
            RuleOptions = new MonitorRuleOptions
            {
                ExpressionScript = "100 > 50",
                ExpressionStatusKey = "high",
            },
        };

        var result = _calculator.Calculate(monitor, new Dictionary<string, double?>());

        Assert.True(result.HasEvent);
        Assert.Equal("high", result.State);
    }

    [Fact]
    public void Calculate_ConstantFalse_NoEvent()
    {
        var monitor = new MonitorConfig
        {
            Id = "test-3",
            RuleType = RuleType.Expression,
            RuleOptions = new MonitorRuleOptions
            {
                ExpressionScript = "50 > 100",
                ExpressionStatusKey = "high",
            },
        };

        var result = _calculator.Calculate(monitor, new Dictionary<string, double?>());

        Assert.False(result.HasEvent);
    }

    // ===== F1: 只替换表达式中出现的变量 =====

    [Fact]
    public void Calculate_OnlyReferencedVariables_Used()
    {
        // data 包含 3 个 tag，但表达式只用到一个 — F1 修复验证
        var monitor = new MonitorConfig
        {
            Id = "test-f1",
            RuleType = RuleType.Expression,
            RuleOptions = new MonitorRuleOptions
            {
                ExpressionScript = "temp > 80",
                ExpressionStatusKey = "high",
            },
        };
        var data = new Dictionary<string, double?>
        {
            ["temp"] = 95.0,
            ["unused1"] = 10.0,
            ["unused2"] = 20.0,
        };

        var result = _calculator.Calculate(monitor, data);

        Assert.True(result.HasEvent);
        Assert.Equal("high", result.State);
    }

    // ===== 含点号的 tag 名 (F1 核心场景) =====

    [Fact]
    public void Calculate_DottedTagName_Works()
    {
        var monitor = new MonitorConfig
        {
            Id = "test-dot",
            RuleType = RuleType.Expression,
            RuleOptions = new MonitorRuleOptions
            {
                ExpressionScript = "db05.test1 > 80",
                ExpressionStatusKey = "high",
            },
        };
        var data = new Dictionary<string, double?>
        {
            ["db05.test1"] = 95.0,
        };

        var result = _calculator.Calculate(monitor, data);

        Assert.True(result.HasEvent);
        Assert.Equal("high", result.State);
    }

    [Fact]
    public void Calculate_MultipleDottedTagNames()
    {
        var monitor = new MonitorConfig
        {
            Id = "test-multidot",
            RuleType = RuleType.Expression,
            RuleOptions = new MonitorRuleOptions
            {
                // DynamicExpresso replaces . with _, so we need to use __v0 > __v1
                ExpressionScript = "db05.tag_a > db05.tag_b",
                ExpressionStatusKey = "high",
            },
        };
        var data = new Dictionary<string, double?>
        {
            ["db05.tag_a"] = 100.0,
            ["db05.tag_b"] = 50.0,
        };

        var result = _calculator.Calculate(monitor, data);

        Assert.True(result.HasEvent);
        Assert.Equal("high", result.State);
    }

    // ===== Math.* 函数 (F2 核心场景) =====

    [Fact]
    public void Calculate_MathAbs_Works()
    {
        var monitor = new MonitorConfig
        {
            Id = "test-abs",
            RuleType = RuleType.Expression,
            RuleOptions = new MonitorRuleOptions
            {
                ExpressionScript = "Math.Abs(x) > 10",
                ExpressionStatusKey = "high",
            },
        };
        var data = new Dictionary<string, double?> { ["x"] = -25.0 };

        var result = _calculator.Calculate(monitor, data);

        Assert.True(result.HasEvent);
    }

    [Fact]
    public void Calculate_MathMax_Works()
    {
        var monitor = new MonitorConfig
        {
            Id = "test-max",
            RuleType = RuleType.Expression,
            RuleOptions = new MonitorRuleOptions
            {
                ExpressionScript = "Math.Max(a, b) > 80",
                ExpressionStatusKey = "high",
            },
        };
        var data = new Dictionary<string, double?> { ["a"] = 90.0, ["b"] = 70.0 };

        var result = _calculator.Calculate(monitor, data);

        Assert.True(result.HasEvent);
    }

    [Fact]
    public void Calculate_MathMin_Works()
    {
        var monitor = new MonitorConfig
        {
            Id = "test-min",
            RuleType = RuleType.Expression,
            RuleOptions = new MonitorRuleOptions
            {
                ExpressionScript = "Math.Min(a, b) < 30",
                ExpressionStatusKey = "low",
            },
        };
        var data = new Dictionary<string, double?> { ["a"] = 50.0, ["b"] = 20.0 };

        var result = _calculator.Calculate(monitor, data);

        Assert.True(result.HasEvent);
    }

    [Fact]
    public void Calculate_MathSqrt_Works()
    {
        var monitor = new MonitorConfig
        {
            Id = "test-sqrt",
            RuleType = RuleType.Expression,
            RuleOptions = new MonitorRuleOptions
            {
                ExpressionScript = "Math.Sqrt(x) > 5",
                ExpressionStatusKey = "high",
            },
        };
        var data = new Dictionary<string, double?> { ["x"] = 100.0 };

        var result = _calculator.Calculate(monitor, data);

        Assert.True(result.HasEvent);
    }

    [Fact]
    public void Calculate_MathPow_Works()
    {
        var monitor = new MonitorConfig
        {
            Id = "test-pow",
            RuleType = RuleType.Expression,
            RuleOptions = new MonitorRuleOptions
            {
                ExpressionScript = "Math.Pow(x, 2) > 100",
                ExpressionStatusKey = "high",
            },
        };
        var data = new Dictionary<string, double?> { ["x"] = 15.0 };

        var result = _calculator.Calculate(monitor, data);

        Assert.True(result.HasEvent);
    }

    [Fact]
    public void Calculate_MathRound_Works()
    {
        var monitor = new MonitorConfig
        {
            Id = "test-round",
            RuleType = RuleType.Expression,
            RuleOptions = new MonitorRuleOptions
            {
                ExpressionScript = "Math.Round(x) > 10",
                ExpressionStatusKey = "high",
            },
        };
        var data = new Dictionary<string, double?> { ["x"] = 15.7 };

        var result = _calculator.Calculate(monitor, data);

        Assert.True(result.HasEvent);
    }

    // ===== 系统命名空间不被当作变量 =====

    [Fact]
    public void Calculate_SystemNamespace_NotTreatedAsVariable()
    {
        // WAS, SISAI, IM, IC, MathE, SuShine 不应被当作变量去 data 查找
        var monitor = new MonitorConfig
        {
            Id = "test-sys",
            RuleType = RuleType.Expression,
            RuleOptions = new MonitorRuleOptions
            {
                ExpressionScript = "Math.Abs(temp - 50) > 10",
                ExpressionStatusKey = "deviated",
            },
        };
        var data = new Dictionary<string, double?> { ["temp"] = 80.0 };

        var result = _calculator.Calculate(monitor, data);

        Assert.True(result.HasEvent);
    }

    // ===== 变量不存在于 data =====

    [Fact]
    public void Calculate_MissingVariable_ReturnsEmpty()
    {
        var monitor = new MonitorConfig
        {
            Id = "test-missing",
            RuleType = RuleType.Expression,
            RuleOptions = new MonitorRuleOptions
            {
                ExpressionScript = "missing_var > 50",
                ExpressionStatusKey = "high",
            },
        };
        var data = new Dictionary<string, double?> { ["other"] = 100.0 };

        var result = _calculator.Calculate(monitor, data);

        Assert.False(result.HasEvent);
    }

    // ===== 复杂表达式 =====

    [Fact]
    public void Calculate_ComplexAndOr_Works()
    {
        var monitor = new MonitorConfig
        {
            Id = "test-complex",
            RuleType = RuleType.Expression,
            RuleOptions = new MonitorRuleOptions
            {
                ExpressionScript = "(a > 10 && b < 5) || (c > 20)",
                ExpressionStatusKey = "triggered",
            },
        };
        var data = new Dictionary<string, double?> { ["a"] = 15.0, ["b"] = 3.0, ["c"] = 10.0 };

        var result = _calculator.Calculate(monitor, data);

        Assert.True(result.HasEvent);
    }

    [Fact]
    public void Calculate_ComplexMathExpression_Works()
    {
        // Math.Abs(a - b) / Math.Max(c, 1) > 2
        var monitor = new MonitorConfig
        {
            Id = "test-complex-math",
            RuleType = RuleType.Expression,
            RuleOptions = new MonitorRuleOptions
            {
                ExpressionScript = "Math.Abs(a - b) / Math.Max(c, 1) > 2",
                ExpressionStatusKey = "high",
            },
        };
        var data = new Dictionary<string, double?> { ["a"] = 100.0, ["b"] = 20.0, ["c"] = 10.0 };

        var result = _calculator.Calculate(monitor, data);

        Assert.True(result.HasEvent);
    }

    [Fact]
    public void Calculate_NestedFunctionCall_Works()
    {
        var monitor = new MonitorConfig
        {
            Id = "test-nested",
            RuleType = RuleType.Expression,
            RuleOptions = new MonitorRuleOptions
            {
                ExpressionScript = "Math.Abs(Math.Max(a, b) - Math.Min(a, b)) > 30",
                ExpressionStatusKey = "wide",
            },
        };
        var data = new Dictionary<string, double?> { ["a"] = 100.0, ["b"] = 20.0 };

        var result = _calculator.Calculate(monitor, data);

        Assert.True(result.HasEvent);
    }

    // ===== 异常处理 =====

    [Fact]
    public void Calculate_InvalidExpression_ReturnsNotSuccess()
    {
        var monitor = new MonitorConfig
        {
            Id = "test-invalid",
            RuleType = RuleType.Expression,
            RuleOptions = new MonitorRuleOptions
            {
                ExpressionScript = "this && is && invalid",
                ExpressionStatusKey = "err",
            },
        };
        var data = new Dictionary<string, double?> { ["x"] = 1.0 };

        var result = _calculator.Calculate(monitor, data);

        Assert.False(result.IsSuccess);
        Assert.NotEmpty(result.Logs);
    }

    // ===== 默认 StatusKey =====

    [Fact]
    public void Calculate_NoCustomStatusKey_UsesDefault()
    {
        var monitor = new MonitorConfig
        {
            Id = "test-default",
            RuleType = RuleType.Expression,
            RuleOptions = new MonitorRuleOptions
            {
                ExpressionScript = "true",
            },
        };
        var data = new Dictionary<string, double?>();

        var result = _calculator.Calculate(monitor, data);

        Assert.True(result.HasEvent);
        Assert.Equal("expression_triggered", result.State);
    }
}
