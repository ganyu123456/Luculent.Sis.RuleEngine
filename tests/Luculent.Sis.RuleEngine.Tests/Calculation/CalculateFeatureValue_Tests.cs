using Luculent.Sis.RuleEngine.Shared.Models;
using Luculent.Sis.RuleEngine.Worker.Calculation.Calculators;
using Microsoft.Extensions.Logging;
using Moq;

namespace Luculent.Sis.RuleEngine.Tests.Calculation;

public class CalculateFeatureValue_Tests
{
    private readonly CalculateFeatureValue _calculator;

    public CalculateFeatureValue_Tests()
    {
        var logger = Mock.Of<ILogger<CalculateFeatureValue>>();
        _calculator = new CalculateFeatureValue(logger);
    }

    [Fact]
    public void Calculate_ValueMatchesTriggerDef_ReturnsEvent()
    {
        var monitor = new MonitorConfig
        {
            Id = "fv-1",
            FocusSourceId = "feature_tag",
            MonitorStatusDefinitions = new List<MonitorStatusDefinition>
            {
                new()
                {
                    Key = "def1",
                    TriggerValueDefDic = new Dictionary<int, string>
                    {
                        [1] = "normal",
                        [2] = "warning",
                        [3] = "alarm",
                    },
                },
            },
        };
        var data = new Dictionary<string, double?> { ["feature_tag"] = 2 };

        var result = _calculator.Calculate(monitor, data);

        Assert.True(result.HasEvent);
        Assert.Equal("warning", result.State);
        Assert.Equal(2.0, result.TriggerValue);
    }

    [Fact]
    public void Calculate_ValueDoesNotMatch_NoEvent()
    {
        var monitor = new MonitorConfig
        {
            Id = "fv-2",
            FocusSourceId = "feature_tag",
            MonitorStatusDefinitions = new List<MonitorStatusDefinition>
            {
                new()
                {
                    Key = "def1",
                    TriggerValueDefDic = new Dictionary<int, string> { [1] = "normal" },
                },
            },
        };
        var data = new Dictionary<string, double?> { ["feature_tag"] = 99 };

        var result = _calculator.Calculate(monitor, data);

        Assert.False(result.HasEvent);
        Assert.Null(result.State);
    }

    [Fact]
    public void Calculate_NoStatusDefs_ReturnsEmpty()
    {
        var monitor = new MonitorConfig
        {
            Id = "fv-3",
            FocusSourceId = "feature_tag",
            MonitorStatusDefinitions = new List<MonitorStatusDefinition>(),
        };
        var data = new Dictionary<string, double?> { ["feature_tag"] = 1 };

        var result = _calculator.Calculate(monitor, data);

        Assert.False(result.HasEvent);
    }

    [Fact]
    public void Calculate_FocusSourceIdEmpty_UsesTagName()
    {
        var monitor = new MonitorConfig
        {
            Id = "fv-4",
            FocusSourceId = "",
            TagName = "tag_a",
            MonitorStatusDefinitions = new List<MonitorStatusDefinition>
            {
                new()
                {
                    Key = "def1",
                    TriggerValueDefDic = new Dictionary<int, string> { [5] = "critical" },
                },
            },
        };
        var data = new Dictionary<string, double?> { ["tag_a"] = 5 };

        var result = _calculator.Calculate(monitor, data);

        Assert.True(result.HasEvent);
        Assert.Equal("critical", result.State);
    }

    [Fact]
    public void Calculate_DataMissing_ReturnsNoSuccess()
    {
        var monitor = new MonitorConfig
        {
            Id = "fv-5",
            FocusSourceId = "missing_tag",
            MonitorStatusDefinitions = new List<MonitorStatusDefinition>
            {
                new()
                {
                    Key = "def1",
                    TriggerValueDefDic = new Dictionary<int, string> { [1] = "normal" },
                },
            },
        };
        var data = new Dictionary<string, double?>();

        var result = _calculator.Calculate(monitor, data);

        Assert.False(result.IsSuccess);
    }
}
