using Luculent.Sis.RuleEngine.Shared.Interfaces;
using Luculent.Sis.RuleEngine.Shared.Models;
using Luculent.Sis.RuleEngine.Worker.Calculation;
using Microsoft.Extensions.Logging;
using Moq;

namespace Luculent.Sis.RuleEngine.Tests.Calculation;

public class PrerulePipeline_Tests
{
    private readonly Mock<IAlarmWriter> _alarmWriterMock;
    private readonly PrerulePipeline _pipeline;

    public PrerulePipeline_Tests()
    {
        _alarmWriterMock = new Mock<IAlarmWriter>();
        var logger = Mock.Of<ILogger<PrerulePipeline>>();
        _pipeline = new PrerulePipeline(_alarmWriterMock.Object, logger);
    }

    [Fact]
    public async Task CheckAsync_PreruleDisabled_ReturnsPass()
    {
        var monitor = new MonitorConfig
        {
            Id = "mon-1",
            Prerule = new PreruleConfig { IsEnabled = false },
        };

        var result = await _pipeline.CheckAsync(monitor);

        Assert.False(result.ShouldSuppress);
    }

    [Fact]
    public async Task CheckAsync_ManualFlagOff_Suppresses()
    {
        var monitor = new MonitorConfig
        {
            Id = "mon-1",
            ManualFlag = 0,
            Prerule = new PreruleConfig { IsEnabled = true, EnableManualFlagCheck = true },
        };

        var result = await _pipeline.CheckAsync(monitor);

        Assert.True(result.ShouldSuppress);
        Assert.True(result.ShouldClearAlarm);
        Assert.Contains("ManualFlag", result.SuppressReason);
    }

    [Fact]
    public async Task CheckAsync_ManualFlagOn_Passes()
    {
        var monitor = new MonitorConfig
        {
            Id = "mon-1",
            ManualFlag = 1,
            Prerule = new PreruleConfig { IsEnabled = true, EnableManualFlagCheck = true },
        };

        var result = await _pipeline.CheckAsync(monitor);

        Assert.False(result.ShouldSuppress);
    }

    [Fact]
    public async Task CheckAsync_ManualFlagCheckDisabled_Passes()
    {
        var monitor = new MonitorConfig
        {
            Id = "mon-1",
            ManualFlag = 0,
            Prerule = new PreruleConfig { IsEnabled = true, EnableManualFlagCheck = false },
        };

        var result = await _pipeline.CheckAsync(monitor);

        Assert.False(result.ShouldSuppress);
    }

    [Fact]
    public async Task CheckAsync_StopMonitorInAlarm_Suppresses()
    {
        var monitor = new MonitorConfig
        {
            Id = "mon-1",
            StopMonitorKey = "stop-mon-1",
            Prerule = new PreruleConfig { IsEnabled = true, EnableStopMonitorCheck = true },
        };
        _alarmWriterMock
            .Setup(x => x.GetAlarmAsync("stop-mon-1"))
            .ReturnsAsync(new AlarmSnapshot { MonitorId = "stop-mon-1", StatusKey = "alarm" });

        var result = await _pipeline.CheckAsync(monitor);

        Assert.True(result.ShouldSuppress);
        Assert.True(result.ShouldClearAlarm);
    }

    [Fact]
    public async Task CheckAsync_StopMonitorNotInAlarm_Passes()
    {
        var monitor = new MonitorConfig
        {
            Id = "mon-1",
            StopMonitorKey = "stop-mon-1",
            Prerule = new PreruleConfig { IsEnabled = true, EnableStopMonitorCheck = true },
        };
        _alarmWriterMock
            .Setup(x => x.GetAlarmAsync("stop-mon-1"))
            .ReturnsAsync((AlarmSnapshot?)null);

        var result = await _pipeline.CheckAsync(monitor);

        Assert.False(result.ShouldSuppress);
    }

    [Fact]
    public async Task CheckAsync_NoStopMonitorKey_Passes()
    {
        var monitor = new MonitorConfig
        {
            Id = "mon-1",
            StopMonitorKey = "",
            Prerule = new PreruleConfig { IsEnabled = true, EnableStopMonitorCheck = true },
        };

        var result = await _pipeline.CheckAsync(monitor);

        Assert.False(result.ShouldSuppress);
    }

    [Fact]
    public async Task CheckAsync_SourceDependencyInAlarm_Suppresses()
    {
        var monitor = new MonitorConfig
        {
            Id = "mon-1",
            MonitorSources = new List<MonitorSourceDefinition>
            {
                new() { Key = "src1", SourceType = 1, RelatedId = "related-1" },
            },
            Prerule = new PreruleConfig
            {
                IsEnabled = true,
                EnableManualFlagCheck = false,
                EnableStopMonitorCheck = false,
                EnableSourceDependencyCheck = true,
            },
        };
        _alarmWriterMock
            .Setup(x => x.GetAlarmAsync("related-1"))
            .ReturnsAsync(new AlarmSnapshot { MonitorId = "related-1", StatusKey = "error" });

        var result = await _pipeline.CheckAsync(monitor);

        Assert.True(result.ShouldSuppress);
    }

    [Fact]
    public async Task CheckAsync_SourceDependencyNotAlarm_Passes()
    {
        var monitor = new MonitorConfig
        {
            Id = "mon-1",
            MonitorSources = new List<MonitorSourceDefinition>
            {
                new() { Key = "src1", SourceType = 1, RelatedId = "related-1" },
            },
            Prerule = new PreruleConfig
            {
                IsEnabled = true,
                EnableManualFlagCheck = false,
                EnableStopMonitorCheck = false,
                EnableSourceDependencyCheck = true,
            },
        };
        _alarmWriterMock
            .Setup(x => x.GetAlarmAsync("related-1"))
            .ReturnsAsync((AlarmSnapshot?)null);

        var result = await _pipeline.CheckAsync(monitor);

        Assert.False(result.ShouldSuppress);
    }

    [Fact]
    public async Task CheckAsync_AllChecksPass_ReturnsPass()
    {
        var monitor = new MonitorConfig
        {
            Id = "mon-1",
            ManualFlag = 1,
            StopMonitorKey = "stop-1",
            MonitorSources = new List<MonitorSourceDefinition>
            {
                new() { Key = "src1", SourceType = 1, RelatedId = "rel-1" },
            },
            Prerule = new PreruleConfig
            {
                IsEnabled = true,
                EnableManualFlagCheck = true,
                EnableStopMonitorCheck = true,
                EnableSourceDependencyCheck = true,
            },
        };
        _alarmWriterMock.Setup(x => x.GetAlarmAsync("stop-1")).ReturnsAsync((AlarmSnapshot?)null);
        _alarmWriterMock.Setup(x => x.GetAlarmAsync("rel-1")).ReturnsAsync((AlarmSnapshot?)null);

        var result = await _pipeline.CheckAsync(monitor);

        Assert.False(result.ShouldSuppress);
    }
}
