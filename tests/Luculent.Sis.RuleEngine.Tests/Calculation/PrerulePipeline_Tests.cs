using Luculent.Sis.RuleEngine.Shared.Interfaces;
using Luculent.Sis.RuleEngine.Shared.Models;
using Luculent.Sis.RuleEngine.Worker.Calculation;
using Luculent.Sis.RuleEngine.Worker.Storage;
using Microsoft.Extensions.Logging;
using Moq;

namespace Luculent.Sis.RuleEngine.Tests.Calculation;

public class PrerulePipeline_Tests
{
    private readonly Mock<IAlarmWriter> _alarmWriterMock;
    private readonly PreruleStateStore _stateStore;
    private readonly PrerulePipeline _pipeline;

    public PrerulePipeline_Tests()
    {
        _alarmWriterMock = new Mock<IAlarmWriter>();
        _stateStore = new PreruleStateStore();
        var logger = Mock.Of<ILogger<PrerulePipeline>>();
        _pipeline = new PrerulePipeline(_stateStore, _alarmWriterMock.Object, logger);
    }

    // ===== PreruleId + PreruleStateStore 检查 =====

    [Fact]
    public async Task CheckAsync_NoPreruleId_NoInterfaceMonitoring_Passes()
    {
        var monitor = new MonitorConfig
        {
            Id = "mon-1",
            PreruleId = null,
        };

        var result = await _pipeline.CheckAsync(monitor);

        Assert.False(result.ShouldSuppress);
    }

    [Fact]
    public async Task CheckAsync_PreruleStateNotReady_SuppressesNoClearAlarm()
    {
        var monitor = new MonitorConfig
        {
            Id = "mon-1",
            PreruleId = "prerule-1",
        };
        // State not set → null (not ready)

        var result = await _pipeline.CheckAsync(monitor);

        Assert.True(result.ShouldSuppress);
        Assert.False(result.ShouldClearAlarm);
    }

    [Fact]
    public async Task CheckAsync_PreruleStateFalse_SuppressesWithClearAlarm()
    {
        var monitor = new MonitorConfig
        {
            Id = "mon-1",
            PreruleId = "prerule-1",
        };
        _stateStore.SetState("prerule-1", false);

        var result = await _pipeline.CheckAsync(monitor);

        Assert.True(result.ShouldSuppress);
        Assert.True(result.ShouldClearAlarm);
        Assert.Contains("不满足前置条件", result.SuppressReason);
    }

    [Fact]
    public async Task CheckAsync_PreruleStateTrue_PassesToInterfaceMonitoring()
    {
        var monitor = new MonitorConfig
        {
            Id = "mon-1",
            PreruleId = "prerule-1",
        };
        _stateStore.SetState("prerule-1", true);

        var result = await _pipeline.CheckAsync(monitor);

        Assert.False(result.ShouldSuppress);
    }

    // ===== InterfaceMonitoring: IsEnabled =====

    [Fact]
    public async Task CheckAsync_InterfaceMonitoringDisabled_Passes()
    {
        var monitor = new MonitorConfig
        {
            Id = "mon-1",
            PreruleId = "prerule-1",
            InterfaceMonitoring = new InterfaceMonitoringConfig { IsEnabled = false },
        };
        _stateStore.SetState("prerule-1", true);

        var result = await _pipeline.CheckAsync(monitor);

        Assert.False(result.ShouldSuppress);
    }

    // ===== ManualFlag =====

    [Fact]
    public async Task CheckAsync_ManualFlagOff_Suppresses()
    {
        var monitor = new MonitorConfig
        {
            Id = "mon-1",
            ManualFlag = 0,
            PreruleId = "prerule-1",
            InterfaceMonitoring = new InterfaceMonitoringConfig { IsEnabled = true, EnableManualFlagCheck = true },
        };
        _stateStore.SetState("prerule-1", true);

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
            PreruleId = "prerule-1",
            InterfaceMonitoring = new InterfaceMonitoringConfig { IsEnabled = true, EnableManualFlagCheck = true },
        };
        _stateStore.SetState("prerule-1", true);

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
            PreruleId = "prerule-1",
            InterfaceMonitoring = new InterfaceMonitoringConfig { IsEnabled = true, EnableManualFlagCheck = false },
        };
        _stateStore.SetState("prerule-1", true);

        var result = await _pipeline.CheckAsync(monitor);

        Assert.False(result.ShouldSuppress);
    }

    // ===== StopMonitor =====

    [Fact]
    public async Task CheckAsync_StopMonitorInAlarm_Suppresses()
    {
        var monitor = new MonitorConfig
        {
            Id = "mon-1",
            StopMonitorKey = "stop-mon-1",
            PreruleId = "prerule-1",
            InterfaceMonitoring = new InterfaceMonitoringConfig { IsEnabled = true, EnableStopMonitorCheck = true },
        };
        _stateStore.SetState("prerule-1", true);
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
            PreruleId = "prerule-1",
            InterfaceMonitoring = new InterfaceMonitoringConfig { IsEnabled = true, EnableStopMonitorCheck = true },
        };
        _stateStore.SetState("prerule-1", true);
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
            PreruleId = "prerule-1",
            InterfaceMonitoring = new InterfaceMonitoringConfig { IsEnabled = true, EnableStopMonitorCheck = true },
        };
        _stateStore.SetState("prerule-1", true);

        var result = await _pipeline.CheckAsync(monitor);

        Assert.False(result.ShouldSuppress);
    }

    // ===== SourceDependency =====

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
            PreruleId = "prerule-1",
            InterfaceMonitoring = new InterfaceMonitoringConfig
            {
                IsEnabled = true,
                EnableManualFlagCheck = false,
                EnableStopMonitorCheck = false,
                EnableSourceDependencyCheck = true,
            },
        };
        _stateStore.SetState("prerule-1", true);
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
            PreruleId = "prerule-1",
            InterfaceMonitoring = new InterfaceMonitoringConfig
            {
                IsEnabled = true,
                EnableManualFlagCheck = false,
                EnableStopMonitorCheck = false,
                EnableSourceDependencyCheck = true,
            },
        };
        _stateStore.SetState("prerule-1", true);
        _alarmWriterMock
            .Setup(x => x.GetAlarmAsync("related-1"))
            .ReturnsAsync((AlarmSnapshot?)null);

        var result = await _pipeline.CheckAsync(monitor);

        Assert.False(result.ShouldSuppress);
    }

    // ===== 综合检查 =====

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
            PreruleId = "prerule-1",
            InterfaceMonitoring = new InterfaceMonitoringConfig
            {
                IsEnabled = true,
                EnableManualFlagCheck = true,
                EnableStopMonitorCheck = true,
                EnableSourceDependencyCheck = true,
            },
        };
        _stateStore.SetState("prerule-1", true);
        _alarmWriterMock.Setup(x => x.GetAlarmAsync("stop-1")).ReturnsAsync((AlarmSnapshot?)null);
        _alarmWriterMock.Setup(x => x.GetAlarmAsync("rel-1")).ReturnsAsync((AlarmSnapshot?)null);

        var result = await _pipeline.CheckAsync(monitor);

        Assert.False(result.ShouldSuppress);
    }

    // ===== Prerule 优先于 InterfaceMonitoring =====

    [Fact]
    public async Task CheckAsync_PreruleFalse_InterfaceMonitoringNotChecked()
    {
        var monitor = new MonitorConfig
        {
            Id = "mon-1",
            ManualFlag = 0,
            PreruleId = "prerule-1",
            InterfaceMonitoring = new InterfaceMonitoringConfig
            {
                IsEnabled = true,
                EnableManualFlagCheck = true,
            },
        };
        _stateStore.SetState("prerule-1", false);

        var result = await _pipeline.CheckAsync(monitor);

        // Prerule false 直接返回，不会走到 ManualFlag 检查
        Assert.True(result.ShouldSuppress);
        Assert.Contains("不满足前置条件", result.SuppressReason);
    }
}
