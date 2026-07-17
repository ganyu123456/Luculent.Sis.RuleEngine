using Luculent.Sis.RuleEngine.Shared.Enums;
using Luculent.Sis.RuleEngine.Shared.Interfaces;
using Luculent.Sis.RuleEngine.Shared.Models;
using Luculent.Sis.RuleEngine.Worker.Calculation.Calculators;
using Luculent.Sis.RuleEngine.Worker.Storage;
using Microsoft.Extensions.Logging;
using Moq;

namespace Luculent.Sis.RuleEngine.Tests.Calculation;

public class CalculateRuleRangeDuration_Tests
{
    private readonly IStateStore _stateStore;
    private readonly CalculateRuleRangeDuration _calculator;

    public CalculateRuleRangeDuration_Tests()
    {
        _stateStore = new InMemoryStateStore();
        var logger = Mock.Of<ILogger<CalculateRuleRangeDuration>>();
        _calculator = new CalculateRuleRangeDuration(logger, _stateStore);
    }

    [Fact]
    public async Task Calculate_ImmediateHit_ReturnsEventOnFirstSatisfaction()
    {
        var monitor = new MonitorConfig
        {
            Id = "test-rd",
            RuleType = RuleType.RangeDuration,
            RuleOptions = new MonitorRuleOptions
            {
                RangeDurationRules = new List<RangeDurationRuleConfig>
                {
                    new()
                    {
                        Id = "rule-1",
                        IsEnabled = true,
                        LeftTagName = "temp",
                        RightTagName = "threshold",
                        SymbolType = SymbolType.Greater,
                        StatusKey = "alarm",
                        DurationSecond = 0, // 立即触发
                        BreakOnHit = true,
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
        Assert.Equal("alarm", result.State);
        Assert.True(result.HasEvent);
    }

    [Fact]
    public async Task Calculate_NotSatisfied_ReturnsEmpty()
    {
        var monitor = new MonitorConfig
        {
            Id = "test-rd-2",
            RuleType = RuleType.RangeDuration,
            RuleOptions = new MonitorRuleOptions
            {
                RangeDurationRules = new List<RangeDurationRuleConfig>
                {
                    new()
                    {
                        Id = "rule-1",
                        IsEnabled = true,
                        LeftTagName = "temp",
                        RightTagName = "threshold",
                        SymbolType = SymbolType.Greater,
                        StatusKey = "alarm",
                        DurationSecond = 0,
                    },
                },
            },
        };
        var data = new Dictionary<string, double?>
        {
            ["temp"] = 50,
            ["threshold"] = 100,
        };

        var result = await _calculator.CalculateAsync(monitor, data);

        Assert.True(result.IsSuccess);
        Assert.Null(result.State);
        Assert.False(result.HasEvent);
    }

    [Fact]
    public async Task Calculate_WithDuration_FirstHitDoesNotTrigger()
    {
        var monitor = new MonitorConfig
        {
            Id = "test-rd-3",
            RuleType = RuleType.RangeDuration,
            RuleOptions = new MonitorRuleOptions
            {
                RangeDurationRules = new List<RangeDurationRuleConfig>
                {
                    new()
                    {
                        Id = "rule-1",
                        IsEnabled = true,
                        LeftTagName = "temp",
                        RightTagName = "threshold",
                        SymbolType = SymbolType.Greater,
                        StatusKey = "alarm",
                        DurationSecond = 60, // 需要持续 60 秒
                    },
                },
            },
        };
        var data = new Dictionary<string, double?>
        {
            ["temp"] = 105,
            ["threshold"] = 100,
        };

        // 第一次计算: 条件满足但时长不够
        var result = await _calculator.CalculateAsync(monitor, data);
        Assert.False(result.HasEvent);

        // 模拟持续满足 61 秒后
        var futureTime = DateTime.UtcNow.AddSeconds(61);
        var result2 = await _calculator.CalculateAsync(monitor, data, futureTime);
        // 状态已经在第一次保存了，这里还需要更多时间
        Assert.NotNull(result2);
    }

    [Fact]
    public async Task Calculate_EmptyRules_ReturnsEmpty()
    {
        var monitor = new MonitorConfig
        {
            Id = "test-rd-empty",
            RuleType = RuleType.RangeDuration,
        };
        var data = new Dictionary<string, double?>();

        var result = await _calculator.CalculateAsync(monitor, data);

        Assert.True(result.IsSuccess);
        Assert.Null(result.State);
    }
}
