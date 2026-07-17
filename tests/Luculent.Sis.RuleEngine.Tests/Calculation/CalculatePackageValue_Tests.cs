using Luculent.Sis.RuleEngine.Shared.Models;
using Luculent.Sis.RuleEngine.Worker.Calculation.Calculators;
using Microsoft.Extensions.Logging;
using Moq;

namespace Luculent.Sis.RuleEngine.Tests.Calculation;

public class CalculatePackageValue_Tests
{
    private readonly CalculatePackageValue _calculator;

    public CalculatePackageValue_Tests()
    {
        var logger = Mock.Of<ILogger<CalculatePackageValue>>();
        _calculator = new CalculatePackageValue(logger);
    }

    [Fact]
    public void Calculate_SingleBitMatches_ReturnsStates()
    {
        var monitor = new MonitorConfig
        {
            Id = "pv-1",
            FocusSourceId = "pack_tag",
            MonitorStatusDefinitions = new List<MonitorStatusDefinition>
            {
                new()
                {
                    Key = "def1",
                    TriggerValueDefDic = new Dictionary<int, string>
                    {
                        [0] = "bit0_alarm",
                        [1] = "bit1_alarm",
                        [2] = "bit2_alarm",
                    },
                },
            },
        };
        // value = 5 = binary 101 → bits 0 and 2 are set
        var data = new Dictionary<string, double?> { ["pack_tag"] = 5 };

        var result = _calculator.Calculate(monitor, data);

        Assert.True(result.HasEvent);
        Assert.Contains("bit0_alarm", result.States);
        Assert.Contains("bit2_alarm", result.States);
        Assert.DoesNotContain("bit1_alarm", result.States);
    }

    [Fact]
    public void Calculate_NoBitsMatch_ReturnsPackageCompleteEvent()
    {
        var monitor = new MonitorConfig
        {
            Id = "pv-2",
            FocusSourceId = "pack_tag",
            MonitorStatusDefinitions = new List<MonitorStatusDefinition>
            {
                new()
                {
                    Key = "def1",
                    TriggerValueDefDic = new Dictionary<int, string>
                    {
                        [4] = "bit4_alarm",
                    },
                },
            },
        };
        // value = 1 → no bit matches
        var data = new Dictionary<string, double?> { ["pack_tag"] = 1 };

        var result = _calculator.Calculate(monitor, data);

        Assert.True(result.HasEvent);
        Assert.Equal("PACKAGECOMPLETEEVENT", result.State);
        Assert.Empty(result.States);
    }

    [Fact]
    public void Calculate_MultipleBitsMatch_CollectsAll()
    {
        var monitor = new MonitorConfig
        {
            Id = "pv-3",
            FocusSourceId = "pack_tag",
            MonitorStatusDefinitions = new List<MonitorStatusDefinition>
            {
                new()
                {
                    Key = "def1",
                    TriggerValueDefDic = new Dictionary<int, string>
                    {
                        [0] = "a",
                        [1] = "b",
                        [2] = "c",
                        [3] = "d",
                    },
                },
            },
        };
        // value = 15 = binary 1111 → all 4 bits match
        var data = new Dictionary<string, double?> { ["pack_tag"] = 15 };

        var result = _calculator.Calculate(monitor, data);

        Assert.True(result.HasEvent);
        Assert.Equal(4, result.States.Count);
        Assert.Contains("a", result.States);
        Assert.Contains("b", result.States);
        Assert.Contains("c", result.States);
        Assert.Contains("d", result.States);
    }

    [Fact]
    public void Calculate_NoStatusDefs_ReturnsEmpty()
    {
        var monitor = new MonitorConfig
        {
            Id = "pv-4",
            MonitorStatusDefinitions = new List<MonitorStatusDefinition>(),
        };
        var data = new Dictionary<string, double?>();

        var result = _calculator.Calculate(monitor, data);

        Assert.False(result.HasEvent);
    }

    [Fact]
    public void Calculate_SkipsEmptyStatusKeys()
    {
        var monitor = new MonitorConfig
        {
            Id = "pv-5",
            FocusSourceId = "pack_tag",
            MonitorStatusDefinitions = new List<MonitorStatusDefinition>
            {
                new()
                {
                    Key = "def1",
                    TriggerValueDefDic = new Dictionary<int, string>
                    {
                        [0] = "",        // empty → skip
                        [1] = "valid",
                    },
                },
            },
        };
        // value = 3 = binary 11 → both bits 0 and 1
        var data = new Dictionary<string, double?> { ["pack_tag"] = 3 };

        var result = _calculator.Calculate(monitor, data);

        Assert.True(result.HasEvent);
        Assert.Single(result.States);
        Assert.Contains("valid", result.States);
    }
}
