using Luculent.Sis.RuleEngine.Shared.Enums;
using Luculent.Sis.RuleEngine.Shared.Interfaces;
using Luculent.Sis.RuleEngine.Shared.Models;
using Luculent.Sis.RuleEngine.Worker.Calculation.Calculators;
using Luculent.Sis.RuleEngine.Worker.Storage;
using Microsoft.Extensions.Logging;
using Moq;

namespace Luculent.Sis.RuleEngine.Tests.Calculation;

public class CalculateRuleRangeFrequency_Tests
{
    private readonly IStateStore _stateStore;
    private readonly CalculateRuleRangeFrequency _calculator;

    public CalculateRuleRangeFrequency_Tests()
    {
        _stateStore = new InMemoryStateStore();
        var logger = Mock.Of<ILogger<CalculateRuleRangeFrequency>>();
        _calculator = new CalculateRuleRangeFrequency(logger, _stateStore);
    }

    [Fact]
    public async Task Calculate_FirstHit_DoesNotTrigger()
    {
        var monitor = new MonitorConfig
        {
            Id = "test-rf-1",
            RuleType = RuleType.RangeFrequency,
            RuleOptions = new MonitorRuleOptions
            {
                RangeFrequencyRules = new List<RangeFrequencyRuleConfig>
                {
                    new()
                    {
                        Id = "rule-1",
                        IsEnabled = true,
                        LeftTagName = "temp",
                        RightTagName = "threshold",
                        SymbolType = SymbolType.Greater,
                        StatusKey = "alarm",
                        FrequencyCount = 5,
                        WindowSeconds = 60,
                    },
                },
            },
        };
        var data = new Dictionary<string, double?>
        {
            ["temp"] = 105,
            ["threshold"] = 100,
        };

        var result = await _calculator.CalculateAsync(monitor, data);

        Assert.True(result.IsSuccess);
        Assert.False(result.HasEvent);
    }

    [Fact]
    public async Task Calculate_EmptyRules_ReturnsEmpty()
    {
        var monitor = new MonitorConfig
        {
            Id = "test-rf-empty",
            RuleType = RuleType.RangeFrequency,
        };
        var data = new Dictionary<string, double?>();

        var result = await _calculator.CalculateAsync(monitor, data);

        Assert.True(result.IsSuccess);
        Assert.Null(result.State);
    }
}
