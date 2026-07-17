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
            RuleType = RuleType.RulePackageValue,
            TagName = "status",
            MonitorStatusDefinitions = new List<MonitorStatusDefinition>
            {
                new()
                {
                    Key = "def1",
                    TriggerValueDefDic = new Dictionary<int, string>
                    {
                        [0] = "bit0_alarm",
                        [1] = "bit1_alarm",
                    },
                },
            },
            RuleOptions = new MonitorRuleOptions
            {
                RulePackageValueRules = new List<RulePackageValueRuleConfig>
                {
                    new()
                    {
                        Id = "rule-1",
                        IsEnabled = true,
                        SourceKey = "status",
                        StartKey = 0,
                        EndKey = 3,
                        StatusKey = "pack_alarm",
                    },
                },
            },
        };
        // bit 0 = 1 → TriggerValueDefDic[0] = "bit0_alarm"
        var data = new Dictionary<string, double?> { ["status"] = 1 };

        var result = _calculator.Calculate(monitor, data);

        Assert.True(result.HasEvent);
        Assert.True(result.StatesDic.ContainsKey("bit0_alarm"));
        Assert.Equal(1L, (long)result.StatesDic["bit0_alarm"].EventValue);
    }

    [Fact]
    public void Calculate_BitDoesNotMatch_ReturnsCompletionEvent()
    {
        var monitor = new MonitorConfig
        {
            Id = "test-pv-2",
            RuleType = RuleType.RulePackageValue,
            TagName = "status",
            MonitorStatusDefinitions = new List<MonitorStatusDefinition>
            {
                new()
                {
                    Key = "def1",
                    TriggerValueDefDic = new Dictionary<int, string>
                    {
                        [0] = "bit0_alarm",
                    },
                },
            },
            RuleOptions = new MonitorRuleOptions
            {
                RulePackageValueRules = new List<RulePackageValueRuleConfig>
                {
                    new()
                    {
                        Id = "rule-1",
                        IsEnabled = true,
                        SourceKey = "status",
                        StartKey = 0,
                        EndKey = 3,
                    },
                },
            },
        };
        // bit 0 = 0 → 无匹配 → PACKAGECOMPLETEEVENT
        var data = new Dictionary<string, double?> { ["status"] = 0 };

        var result = _calculator.Calculate(monitor, data);

        Assert.True(result.HasEvent);
        Assert.Equal("PACKAGECOMPLETEEVENT", result.State);
    }
}
