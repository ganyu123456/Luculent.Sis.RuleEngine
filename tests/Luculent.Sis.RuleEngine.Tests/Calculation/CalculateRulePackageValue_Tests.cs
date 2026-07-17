using Luculent.Sis.RuleEngine.Shared.Enums;
using Luculent.Sis.RuleEngine.Shared.Models;
using Luculent.Sis.RuleEngine.Worker.Calculation.Calculators;
using Microsoft.Extensions.Logging;
using Moq;

namespace Luculent.Sis.RuleEngine.Tests.Calculation;

public class CalculateRulePackageValue_Tests
{
    private readonly CalculateRulePackageValue _calculator;

    public CalculateRulePackageValue_Tests()
    {
        var logger = Mock.Of<ILogger<CalculateRulePackageValue>>();
        _calculator = new CalculateRulePackageValue(logger);
    }

    [Fact]
    public void Calculate_BitMatches_ReturnsEvent()
    {
        var monitor = new MonitorConfig
        {
            Id = "test-pv",
            RuleType = RuleType.PackageValue,
            TagName = "status",
            RuleOptions = new MonitorRuleOptions
            {
                PackageValueRules = new List<PackageValueRuleConfig>
                {
                    new()
                    {
                        Id = "rule-1",
                        IsEnabled = true,
                        BitPosition = 0,
                        BitLength = 1,
                        ExpectedValue = 1,
                        StatusKey = "bit0_alarm",
                    },
                },
            },
        };
        // bit 0 = 1
        var data = new Dictionary<string, double?> { ["status"] = 1 };

        var result = _calculator.Calculate(monitor, data);

        Assert.Equal("bit0_alarm", result.State);
        Assert.True(result.HasEvent);
    }

    [Fact]
    public void Calculate_BitDoesNotMatch_ReturnsEmpty()
    {
        var monitor = new MonitorConfig
        {
            Id = "test-pv-2",
            RuleType = RuleType.PackageValue,
            TagName = "status",
            RuleOptions = new MonitorRuleOptions
            {
                PackageValueRules = new List<PackageValueRuleConfig>
                {
                    new()
                    {
                        Id = "rule-1",
                        IsEnabled = true,
                        BitPosition = 0,
                        BitLength = 1,
                        ExpectedValue = 1,
                        StatusKey = "bit0_alarm",
                    },
                },
            },
        };
        // bit 0 = 0
        var data = new Dictionary<string, double?> { ["status"] = 0 };

        var result = _calculator.Calculate(monitor, data);

        Assert.Null(result.State);
        Assert.False(result.HasEvent);
    }
}
