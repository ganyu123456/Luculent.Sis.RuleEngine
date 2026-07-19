using System.Collections.Concurrent;
using Luculent.Sis.RuleEngine.Shared.Interfaces;
using Luculent.Sis.RuleEngine.Shared.Models;
using Luculent.Sis.RuleEngine.Worker;
using Luculent.Sis.RuleEngine.Worker.Calculation;
using Luculent.Sis.RuleEngine.Worker.DataAcquisition;
using Luculent.Sis.RuleEngine.Worker.Storage;
using Microsoft.Extensions.Logging;
using Moq;

namespace Luculent.Sis.RuleEngine.Tests;

public class WorkerCalculationService_Tests
{
    private readonly Mock<IStateStore> _stateStoreMock;
    private readonly Mock<IAlarmWriter> _alarmWriterMock;
    private readonly Mock<IRuleDispatcher> _dispatcherMock;
    private readonly Mock<IPrerulePipeline> _preruleMock;
    private readonly WorkerCalculationService _service;

    public WorkerCalculationService_Tests()
    {
        _stateStoreMock = new Mock<IStateStore>();
        _alarmWriterMock = new Mock<IAlarmWriter>();
        _dispatcherMock = new Mock<IRuleDispatcher>();
        _preruleMock = new Mock<IPrerulePipeline>();

        var preruleStateStore = new PreruleStateStore();
        var tagValueStore = new TagValueStore();

        _service = new WorkerCalculationService(
            _stateStoreMock.Object,
            _alarmWriterMock.Object,
            _dispatcherMock.Object,
            _preruleMock.Object,
            new PreruleEvaluationService(
                new PreruleDefinitionStore(),
                preruleStateStore,
                tagValueStore,
                Mock.Of<ILogger<PreruleEvaluationService>>()),
            tagValueStore,
            preruleStateStore,
            null,
            Mock.Of<ILogger<WorkerCalculationService>>())
        {
            WorkerId = "test-worker"
        };
    }

    private static MonitorConfig CreateMonitor(string id = "mon-1") => new()
    {
        Id = id,
        Key = $"key-{id}",
        Name = $"Monitor {id}",
        RefreshIntervalSecond = 30,
        TagName = "tag1",
        RuleOptions = new MonitorRuleOptions(),
        InterfaceMonitoring = new InterfaceMonitoringConfig { IsEnabled = true },
        MonitorSources = new List<MonitorSourceDefinition>
        {
            new() { Key = "src1", SourceType = 3, RelatedId = "rel-1", Unit = "%" }
        }
    };

    private Task InvokeAsync(MonitorConfig monitor, IDictionary<string, double?> values, DateTime now,
        CalculationState? preloadedState = null, ConcurrentDictionary<string, CalculationState>? modifiedStates = null)
    {
        modifiedStates ??= new();
        var method = typeof(WorkerCalculationService).GetMethod("ProcessMonitorAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        return (Task)method.Invoke(_service, [monitor, values, now, preloadedState, modifiedStates, CancellationToken.None])!;
    }

    [Fact]
    public async Task ProcessMonitor_StateChange_WritesHistoryAndSavesState()
    {
        var monitor = CreateMonitor();
        var now = DateTime.UtcNow;
        var values = new Dictionary<string, double?> { ["tag1"] = 95.0 };
        var preloaded = new CalculationState { MonitorId = monitor.Id, PreviousStatus = null };
        var modifiedStates = new ConcurrentDictionary<string, CalculationState>();

        _preruleMock.Setup(p => p.CheckAsync(monitor))
            .ReturnsAsync(new PreruleCheckResult { ShouldSuppress = false });
        _dispatcherMock.Setup(d => d.CalculateAsync(monitor, values, now))
            .ReturnsAsync(new RuleCalculateResult { HasEvent = true, State = "satisfiled", TriggerValue = 95.0 });

        await InvokeAsync(monitor, values, now, preloaded, modifiedStates);

        _alarmWriterMock.Verify(a => a.WriteRealtimeAlarmAsync(
            It.Is<AlarmSnapshot>(s => s.StatusKey == "satisfiled")), Times.AtLeastOnce);
        _alarmWriterMock.Verify(a => a.WriteHistoryAlarmAsync(
            It.Is<AlarmEvent>(e => e.StatusKey == "satisfiled"
                && e.Unit == "%"
                && !string.IsNullOrEmpty(e.JobId)
                && e.TriggerValue == 95.0)), Times.Once);

        // 状态变更通过 modifiedStates 批量保存
        Assert.True(modifiedStates.TryGetValue(monitor.Id, out var saved));
        Assert.Equal("satisfiled", saved.PreviousStatus);
    }

    [Fact]
    public async Task ProcessMonitor_SameStatus_NoDuplicateHistory()
    {
        var monitor = CreateMonitor();
        var now = DateTime.UtcNow;
        var values = new Dictionary<string, double?> { ["tag1"] = 95.0 };
        var preloaded = new CalculationState { MonitorId = monitor.Id, PreviousStatus = "satisfiled", MaxValue = 95.0, MinValue = 95.0 };
        var modifiedStates = new ConcurrentDictionary<string, CalculationState>();

        _preruleMock.Setup(p => p.CheckAsync(monitor))
            .ReturnsAsync(new PreruleCheckResult { ShouldSuppress = false });
        _dispatcherMock.Setup(d => d.CalculateAsync(monitor, values, now))
            .ReturnsAsync(new RuleCalculateResult { HasEvent = true, State = "satisfiled", TriggerValue = 95.0 });

        await InvokeAsync(monitor, values, now, preloaded, modifiedStates);

        // 状态未变化 → 实时报警和历史事件都不写入
        _alarmWriterMock.Verify(a => a.WriteRealtimeAlarmAsync(
            It.IsAny<AlarmSnapshot>()), Times.Never);
        _alarmWriterMock.Verify(a => a.WriteHistoryAlarmAsync(
            It.IsAny<AlarmEvent>()), Times.Never);

        // max/min 也未变化，modifiedStates 不含此 monitor
        Assert.False(modifiedStates.ContainsKey(monitor.Id));
    }

    [Fact]
    public async Task ProcessMonitor_ClearEvent_WritesEmptyStatusAndClearsRealtime()
    {
        var monitor = CreateMonitor();
        var now = DateTime.UtcNow;
        var values = new Dictionary<string, double?> { ["tag1"] = 10.0 };
        var preloaded = new CalculationState { MonitorId = monitor.Id, PreviousStatus = "satisfiled" };
        var modifiedStates = new ConcurrentDictionary<string, CalculationState>();

        _preruleMock.Setup(p => p.CheckAsync(monitor))
            .ReturnsAsync(new PreruleCheckResult { ShouldSuppress = false });
        _dispatcherMock.Setup(d => d.CalculateAsync(monitor, values, now))
            .ReturnsAsync(new RuleCalculateResult { HasEvent = false });

        await InvokeAsync(monitor, values, now, preloaded, modifiedStates);

        _alarmWriterMock.Verify(a => a.WriteHistoryAlarmAsync(
            It.Is<AlarmEvent>(e => e.StatusKey == "")), Times.Once);
        _alarmWriterMock.Verify(a => a.ClearRealtimeAlarmAsync(monitor.Id), Times.Once);

        Assert.True(modifiedStates.TryGetValue(monitor.Id, out var saved));
        Assert.Equal("", saved.PreviousStatus);
    }

    [Fact]
    public async Task ProcessMonitor_AfterClear_NoDuplicateEventNextCycle()
    {
        var monitor = CreateMonitor();
        var now = DateTime.UtcNow;
        var values = new Dictionary<string, double?> { ["tag1"] = 10.0 };
        var preloaded = new CalculationState { MonitorId = monitor.Id, PreviousStatus = "" };
        var modifiedStates = new ConcurrentDictionary<string, CalculationState>();

        _preruleMock.Setup(p => p.CheckAsync(monitor))
            .ReturnsAsync(new PreruleCheckResult { ShouldSuppress = false });
        _dispatcherMock.Setup(d => d.CalculateAsync(monitor, values, now))
            .ReturnsAsync(new RuleCalculateResult { HasEvent = false });

        await InvokeAsync(monitor, values, now, preloaded, modifiedStates);

        // 状态未变化 (已是正常态) → 不写历史事件，也不清除实时报警
        _alarmWriterMock.Verify(a => a.WriteHistoryAlarmAsync(
            It.IsAny<AlarmEvent>()), Times.Never);
        _alarmWriterMock.Verify(a => a.ClearRealtimeAlarmAsync(It.IsAny<string>()), Times.Never);

        // 无状态变更
        Assert.False(modifiedStates.ContainsKey(monitor.Id));
    }

    [Fact]
    public async Task ProcessMonitor_PereruleSuppressWithClear_WritesEmptyStatus()
    {
        var monitor = CreateMonitor();
        var now = DateTime.UtcNow;
        var values = new Dictionary<string, double?> { ["tag1"] = 0.0 };
        var preloaded = new CalculationState { MonitorId = monitor.Id, PreviousStatus = "satisfiled" };
        var modifiedStates = new ConcurrentDictionary<string, CalculationState>();

        _preruleMock.Setup(p => p.CheckAsync(monitor))
            .ReturnsAsync(new PreruleCheckResult { ShouldSuppress = true, ShouldClearAlarm = true });

        await InvokeAsync(monitor, values, now, preloaded, modifiedStates);

        _alarmWriterMock.Verify(a => a.WriteHistoryAlarmAsync(
            It.Is<AlarmEvent>(e => e.StatusKey == "")), Times.Once);
        _alarmWriterMock.Verify(a => a.ClearRealtimeAlarmAsync(monitor.Id), Times.Once);

        Assert.True(modifiedStates.TryGetValue(monitor.Id, out var saved));
        Assert.Equal("", saved.PreviousStatus);
    }

    [Fact]
    public async Task ProcessMonitor_PereruleSuppressNoClear_NoEvent()
    {
        var monitor = CreateMonitor();
        var now = DateTime.UtcNow;
        var values = new Dictionary<string, double?> { ["tag1"] = 0.0 };
        var preloaded = new CalculationState { MonitorId = monitor.Id, PreviousStatus = "satisfiled" };
        var modifiedStates = new ConcurrentDictionary<string, CalculationState>();

        _preruleMock.Setup(p => p.CheckAsync(monitor))
            .ReturnsAsync(new PreruleCheckResult { ShouldSuppress = true, ShouldClearAlarm = false });

        await InvokeAsync(monitor, values, now, preloaded, modifiedStates);

        _alarmWriterMock.Verify(a => a.WriteHistoryAlarmAsync(
            It.IsAny<AlarmEvent>()), Times.Never);
        _alarmWriterMock.Verify(a => a.ClearRealtimeAlarmAsync(It.IsAny<string>()), Times.Never);
        Assert.False(modifiedStates.ContainsKey(monitor.Id));
    }

    [Fact]
    public async Task ProcessMonitor_FirstCalculation_NoSpuriousEvent()
    {
        var monitor = CreateMonitor();
        var now = DateTime.UtcNow;
        var values = new Dictionary<string, double?> { ["tag1"] = 50.0 };
        var preloaded = new CalculationState { MonitorId = monitor.Id, PreviousStatus = "" };
        var modifiedStates = new ConcurrentDictionary<string, CalculationState>();

        _preruleMock.Setup(p => p.CheckAsync(monitor))
            .ReturnsAsync(new PreruleCheckResult { ShouldSuppress = false });
        _dispatcherMock.Setup(d => d.CalculateAsync(monitor, values, now))
            .ReturnsAsync(new RuleCalculateResult { HasEvent = false });

        await InvokeAsync(monitor, values, now, preloaded, modifiedStates);

        _alarmWriterMock.Verify(a => a.WriteHistoryAlarmAsync(
            It.IsAny<AlarmEvent>()), Times.Never);
    }
}
