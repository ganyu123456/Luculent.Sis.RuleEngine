using Luculent.Sis.RuleEngine.Shared.Enums;
using Luculent.Sis.RuleEngine.Shared.Interfaces;
using Luculent.Sis.RuleEngine.Shared.Models;
using Luculent.Sis.RuleEngine.Worker.Calculation.Calculators;
using Microsoft.Extensions.Logging;
using Moq;

namespace Luculent.Sis.RuleEngine.Tests.Calculation;

public class CalculateInterfaceMonitoring_Tests
{
    private readonly Mock<IStateStore> _stateStoreMock;
    private readonly CalculateInterfaceMonitoring _calculator;

    public CalculateInterfaceMonitoring_Tests()
    {
        _stateStoreMock = new Mock<IStateStore>();
        var logger = Mock.Of<ILogger<CalculateInterfaceMonitoring>>();
        _calculator = new CalculateInterfaceMonitoring(logger, _stateStoreMock.Object);
    }

    [Fact]
    public async Task CalculateAsync_ManualFlagZero_ReturnsManualStop()
    {
        var monitor = new MonitorConfig
        {
            Id = "if-1",
            ManualFlag = 0,
            RuleOptions = new MonitorRuleOptions
            {
                InterfaceMonitoringOpts = new InterfaceMonitoringOptions(),
            },
        };
        var data = new Dictionary<string, double?> { ["tag1"] = 1.0 };

        var result = await _calculator.CalculateAsync(monitor, data);

        Assert.True(result.HasEvent);
        Assert.Equal("ManualStop", result.State);
        Assert.Equal("ManualStop", result.InterfaceMonitorType);
    }

    [Fact]
    public async Task CalculateAsync_NoOptions_ReturnsEmpty()
    {
        var monitor = new MonitorConfig
        {
            Id = "if-2",
            RuleOptions = new MonitorRuleOptions(),
        };
        var data = new Dictionary<string, double?>();

        var result = await _calculator.CalculateAsync(monitor, data);

        Assert.False(result.HasEvent);
    }

    [Fact]
    public async Task CalculateAsync_EmptyData_ReturnsInterfaceError()
    {
        var monitor = new MonitorConfig
        {
            Id = "if-3",
            ManualFlag = 1,
            FailureCount = 3,
            RuleOptions = new MonitorRuleOptions
            {
                InterfaceMonitoringOpts = new InterfaceMonitoringOptions(),
            },
        };
        _stateStoreMock.Setup(x => x.GetAsync("if-3")).ReturnsAsync((CalculationState?)null);
        _stateStoreMock.Setup(x => x.SaveAsync("if-3", It.IsAny<CalculationState>()))
            .Returns(Task.CompletedTask);

        var data = new Dictionary<string, double?>();

        var result = await _calculator.CalculateAsync(monitor, data);

        Assert.True(result.HasEvent);
        Assert.Equal("InterfaceError", result.State);
        Assert.Equal(-1, result.TriggerValue);
    }

    [Fact]
    public async Task CalculateAsync_InsufficientSamples_ReturnsNormal()
    {
        var monitor = new MonitorConfig
        {
            Id = "if-4",
            ManualFlag = 1,
            FailureCount = 5,
            RuleOptions = new MonitorRuleOptions
            {
                InterfaceMonitoringOpts = new InterfaceMonitoringOptions(),
            },
        };
        _stateStoreMock.Setup(x => x.GetAsync("if-4")).ReturnsAsync((CalculationState?)null);
        _stateStoreMock.Setup(x => x.SaveAsync("if-4", It.IsAny<CalculationState>()))
            .Returns(Task.CompletedTask);

        // Only 2 samples → less than failureCount (5)
        var data = new Dictionary<string, double?> { ["tag_a"] = 1.0 };

        var result = await _calculator.CalculateAsync(monitor, data);

        Assert.False(result.HasEvent);
        Assert.Equal("Normal", result.InterfaceMonitorType);
    }

    [Fact]
    public async Task CalculateAsync_NormalState_ClearsOnSufficientVariedSamples()
    {
        var monitor = new MonitorConfig
        {
            Id = "if-5",
            ManualFlag = 1,
            FailureCount = 3,
            RuleOptions = new MonitorRuleOptions
            {
                InterfaceMonitoringOpts = new InterfaceMonitoringOptions(),
            },
        };

        // Pre-populate state with 2 samples of varying values
        var existingState = new CalculationState
        {
            MonitorId = "if-5",
            RuleType = RuleType.InterfaceMonitoring,
            InterfaceSamples = new Dictionary<string, List<TagSample>>
            {
                ["tag_a"] = new()
                {
                    new TagSample { TagName = "tag_a", Time = DateTime.UtcNow.AddSeconds(-2), Value = 1.0 },
                    new TagSample { TagName = "tag_a", Time = DateTime.UtcNow.AddSeconds(-1), Value = 2.0 },
                },
            },
        };
        _stateStoreMock.Setup(x => x.GetAsync("if-5")).ReturnsAsync(existingState);
        _stateStoreMock.Setup(x => x.SaveAsync("if-5", It.IsAny<CalculationState>()))
            .Returns(Task.CompletedTask);

        // 3rd sample with different value → values have >1 distinct → Normal
        var data = new Dictionary<string, double?> { ["tag_a"] = 3.0 };

        var result = await _calculator.CalculateAsync(monitor, data);

        Assert.False(result.HasEvent);
        Assert.Equal("Normal", result.InterfaceMonitorType);
    }
}
