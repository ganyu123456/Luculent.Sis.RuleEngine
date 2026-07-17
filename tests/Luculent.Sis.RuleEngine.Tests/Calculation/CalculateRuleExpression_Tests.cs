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
    public void Calculate_WithValidData_DoesNotThrow()
    {
        var monitor = new MonitorConfig
        {
            Id = "test-2",
            RuleType = RuleType.Expression,
            RuleOptions = new MonitorRuleOptions { ExpressionScript = "100 > 50" },
        };
        var data = new Dictionary<string, double?> { ["temp"] = 100 };

        var result = _calculator.Calculate(monitor, data);

        Assert.NotNull(result);
    }
}
