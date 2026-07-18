using Luculent.Sis.RuleEngine.Shared.Enums;
using Luculent.Sis.RuleEngine.Shared.Interfaces;
using Luculent.Sis.RuleEngine.Shared.Models;
using Luculent.Sis.RuleEngine.Worker;
using Luculent.Sis.RuleEngine.Worker.Calculation;
using Microsoft.Extensions.Logging;
using Moq;

namespace Luculent.Sis.RuleEngine.Tests;

public class WorkerCalculationService_Tests
{
    private readonly Mock<ITrendDataReader> _trendReaderMock;
    private readonly Mock<IStateStore> _stateStoreMock;
    private readonly Mock<IAlarmWriter> _alarmWriterMock;
    private readonly Mock<IRuleDispatcher> _dispatcherMock;
    private readonly Mock<IPrerulePipeline> _preruleMock;
    private readonly WorkerCalculationService _service;

    public WorkerCalculationService_Tests()
    {
        _trendReaderMock = new Mock<ITrendDataReader>();
        _stateStoreMock = new Mock<IStateStore>();
        _alarmWriterMock = new Mock<IAlarmWriter>();
        _dispatcherMock = new Mock<IRuleDispatcher>();
        _preruleMock = new Mock<IPrerulePipeline>();

        _service = new WorkerCalculationService(
            _trendReaderMock.Object,
            _stateStoreMock.Object,
            _alarmWriterMock.Object,
            _dispatcherMock.Object,
            _preruleMock.Object,
            Mock.Of<ILogger<WorkerCalculationService>>())
        {
            WorkerId = "test-worker"
        };
    }

    private static MonitorConfig CreateMonitor(string id = "mon-1")
    {
        return new MonitorConfig
        {
            Id = id,
            Key = $"key-{id}",
            Name = $"Monitor {id}",
            RefreshIntervalSecond = 30,
            TagName = "tag1",
            RuleOptions = new MonitorRuleOptions(),
            Prerule = new PreruleConfig { IsEnabled = true },
            MonitorSources = new List<MonitorSourceDefinition>
            {
                new() { Key = "src1", SourceType = 3, RelatedId = "rel-1", Unit = "%" }
            }
        };
    }

    /// <summary>
    /// trigger 事件正确写入：isNewAlarm 时应产生历史事件，同时保存状态。
    /// </summary>
    [Fact]
    public async Task ProcessMonitor_Trigger_IsNewAlarm_WritesHistoryAndSavesState()
    {
        var monitor = CreateMonitor();
        var now = DateTime.UtcNow;
        var values = new Dictionary<string, double?> { ["tag1"] = 95.0 };
        var state = new CalculationState { MonitorId = monitor.Id, PreviousStatus = null };

        _preruleMock.Setup(p => p.CheckAsync(monitor))
            .ReturnsAsync(new PreruleCheckResult { ShouldSuppress = false });

        _dispatcherMock.Setup(d => d.CalculateAsync(monitor, values, now))
            .ReturnsAsync(new RuleCalculateResult { HasEvent = true, State = "satisfiled", TriggerValue = 95.0 });

        _stateStoreMock.Setup(s => s.GetAsync(monitor.Id)).ReturnsAsync(state);
        _alarmWriterMock.Setup(a => a.GetAlarmAsync(monitor.Id)).ReturnsAsync((AlarmSnapshot?)null);

        // Use reflection to invoke private ProcessMonitorAsync
        var method = typeof(WorkerCalculationService).GetMethod("ProcessMonitorAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        await (Task)method.Invoke(_service, [monitor, values, now, CancellationToken.None])!;

        // Verify: realtime alarm written
        _alarmWriterMock.Verify(a => a.WriteRealtimeAlarmAsync(
            It.Is<AlarmSnapshot>(s => s.StatusKey == "satisfiled")), Times.AtLeastOnce);

        // Verify: history alarm written (ToAlarmEvent is used internally via AlarmSnapshot)
        _alarmWriterMock.Verify(a => a.WriteHistoryAlarmAsync(
            It.Is<AlarmEvent>(e => e.EventType == EventType.Trigger
                && e.StatusKey == "satisfiled"
                && e.Unit == "%"
                && !string.IsNullOrEmpty(e.JobId))), Times.Once);

        // Verify: state saved with PreviousStatus and PreviousEventOccurTime
        _stateStoreMock.Verify(s => s.SaveAsync(monitor.Id,
            It.Is<CalculationState>(st => st.PreviousStatus == "satisfiled"
                && st.PreviousEventOccurTime.HasValue)), Times.Once);
    }

    /// <summary>
    /// 同状态不重复写历史事件：isNewAlarm 为 false 时不写 history。
    /// </summary>
    [Fact]
    public async Task ProcessMonitor_SameStatus_NoDuplicateHistory()
    {
        var monitor = CreateMonitor();
        var now = DateTime.UtcNow;
        var values = new Dictionary<string, double?> { ["tag1"] = 95.0 };
        var state = new CalculationState { MonitorId = monitor.Id, PreviousStatus = "satisfiled" };

        _preruleMock.Setup(p => p.CheckAsync(monitor))
            .ReturnsAsync(new PreruleCheckResult { ShouldSuppress = false });

        _dispatcherMock.Setup(d => d.CalculateAsync(monitor, values, now))
            .ReturnsAsync(new RuleCalculateResult { HasEvent = true, State = "satisfiled", TriggerValue = 95.0 });

        _stateStoreMock.Setup(s => s.GetAsync(monitor.Id)).ReturnsAsync(state);

        // 已存在同状态报警
        _alarmWriterMock.Setup(a => a.GetAlarmAsync(monitor.Id))
            .ReturnsAsync(new AlarmSnapshot { StatusKey = "satisfiled" });

        var method = typeof(WorkerCalculationService).GetMethod("ProcessMonitorAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        await (Task)method.Invoke(_service, [monitor, values, now, CancellationToken.None])!;

        // Verify: realtime alarm written (always)
        _alarmWriterMock.Verify(a => a.WriteRealtimeAlarmAsync(
            It.IsAny<AlarmSnapshot>()), Times.AtLeastOnce);

        // Verify: no NEW history event (same status already active)
        _alarmWriterMock.Verify(a => a.WriteHistoryAlarmAsync(
            It.IsAny<AlarmEvent>()), Times.Never);
    }

    /// <summary>
    /// clear 事件正确写入并重置 PreviousStatus，避免后续周期重复 clear。
    /// </summary>
    [Fact]
    public async Task ProcessMonitor_ClearEvent_ResetsStateAndWritesClear()
    {
        var monitor = CreateMonitor();
        var now = DateTime.UtcNow;
        var triggerTime = now.AddMinutes(-5);
        var values = new Dictionary<string, double?> { ["tag1"] = 10.0 };
        var state = new CalculationState
        {
            MonitorId = monitor.Id,
            PreviousStatus = "satisfiled",
            PreviousEventOccurTime = triggerTime
        };

        _preruleMock.Setup(p => p.CheckAsync(monitor))
            .ReturnsAsync(new PreruleCheckResult { ShouldSuppress = false });

        // 无事件 → 报警消除
        _dispatcherMock.Setup(d => d.CalculateAsync(monitor, values, now))
            .ReturnsAsync(new RuleCalculateResult { HasEvent = false });

        _stateStoreMock.Setup(s => s.GetAsync(monitor.Id)).ReturnsAsync(state);

        var method = typeof(WorkerCalculationService).GetMethod("ProcessMonitorAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        await (Task)method.Invoke(_service, [monitor, values, now, CancellationToken.None])!;

        // Verify: clear event written with RelatedTriggerOccurTime
        _alarmWriterMock.Verify(a => a.WriteHistoryAlarmAsync(
            It.Is<AlarmEvent>(e => e.EventType == EventType.Clear
                && e.StatusKey == "satisfiled"
                && e.RelatedTriggerOccurTime == triggerTime)), Times.Once);

        // Verify: realtime alarm cleared
        _alarmWriterMock.Verify(a => a.ClearRealtimeAlarmAsync(monitor.Id), Times.Once);

        // Verify: state saved with PreviousStatus = null (prevent duplicate clear)
        _stateStoreMock.Verify(s => s.SaveAsync(monitor.Id,
            It.Is<CalculationState>(st => st.PreviousStatus == null
                && st.PreviousEventOccurTime == null)), Times.Once);
    }

    /// <summary>
    /// 连续两周期不产生重复 clear：第一个周期清除后，第二个周期不再写 clear。
    /// </summary>
    [Fact]
    public async Task ProcessMonitor_AfterClear_NoDuplicateClearNextCycle()
    {
        var monitor = CreateMonitor();
        var now = DateTime.UtcNow;
        var values = new Dictionary<string, double?> { ["tag1"] = 10.0 };

        // 状态已经在上一个周期被重置为 null
        var state = new CalculationState
        {
            MonitorId = monitor.Id,
            PreviousStatus = null,
            PreviousEventOccurTime = null
        };

        _preruleMock.Setup(p => p.CheckAsync(monitor))
            .ReturnsAsync(new PreruleCheckResult { ShouldSuppress = false });

        _dispatcherMock.Setup(d => d.CalculateAsync(monitor, values, now))
            .ReturnsAsync(new RuleCalculateResult { HasEvent = false });

        _stateStoreMock.Setup(s => s.GetAsync(monitor.Id)).ReturnsAsync(state);

        var method = typeof(WorkerCalculationService).GetMethod("ProcessMonitorAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        await (Task)method.Invoke(_service, [monitor, values, now, CancellationToken.None])!;

        // Verify: no clear event (PreviousStatus already null)
        _alarmWriterMock.Verify(a => a.WriteHistoryAlarmAsync(
            It.Is<AlarmEvent>(e => e.EventType == EventType.Clear)), Times.Never);

        _alarmWriterMock.Verify(a => a.ClearRealtimeAlarmAsync(It.IsAny<string>()), Times.Never);
    }

    /// <summary>
    /// prerule suppress 触发 clear 后也重置状态，避免后续重复 clear。
    /// </summary>
    [Fact]
    public async Task ProcessMonitor_PereruleSuppressWithClear_ResetsState()
    {
        var monitor = CreateMonitor();
        var now = DateTime.UtcNow;
        var triggerTime = now.AddMinutes(-10);
        var values = new Dictionary<string, double?> { ["tag1"] = 0.0 };
        var state = new CalculationState
        {
            MonitorId = monitor.Id,
            PreviousStatus = "satisfiled",
            PreviousEventOccurTime = triggerTime
        };

        _preruleMock.Setup(p => p.CheckAsync(monitor))
            .ReturnsAsync(new PreruleCheckResult { ShouldSuppress = true, ShouldClearAlarm = true });

        _stateStoreMock.Setup(s => s.GetAsync(monitor.Id)).ReturnsAsync(state);

        var method = typeof(WorkerCalculationService).GetMethod("ProcessMonitorAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        await (Task)method.Invoke(_service, [monitor, values, now, CancellationToken.None])!;

        // Verify: clear event with RelatedTriggerOccurTime
        _alarmWriterMock.Verify(a => a.WriteHistoryAlarmAsync(
            It.Is<AlarmEvent>(e => e.EventType == EventType.Clear
                && e.RelatedTriggerOccurTime == triggerTime)), Times.Once);

        // Verify: realtime alarm cleared
        _alarmWriterMock.Verify(a => a.ClearRealtimeAlarmAsync(monitor.Id), Times.Once);

        // Verify: state reset
        _stateStoreMock.Verify(s => s.SaveAsync(monitor.Id,
            It.Is<CalculationState>(st => st.PreviousStatus == null
                && st.PreviousEventOccurTime == null)), Times.Once);
    }

    /// <summary>
    /// prerule suppress 但不要求 clear 时，不产生 clear 事件。
    /// </summary>
    [Fact]
    public async Task ProcessMonitor_PereruleSuppressNoClear_NoClearEvent()
    {
        var monitor = CreateMonitor();
        var now = DateTime.UtcNow;
        var values = new Dictionary<string, double?> { ["tag1"] = 0.0 };
        var state = new CalculationState
        {
            MonitorId = monitor.Id,
            PreviousStatus = "satisfiled",
            PreviousEventOccurTime = now.AddMinutes(-5)
        };

        _preruleMock.Setup(p => p.CheckAsync(monitor))
            .ReturnsAsync(new PreruleCheckResult { ShouldSuppress = true, ShouldClearAlarm = false });

        _stateStoreMock.Setup(s => s.GetAsync(monitor.Id)).ReturnsAsync(state);

        var method = typeof(WorkerCalculationService).GetMethod("ProcessMonitorAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        await (Task)method.Invoke(_service, [monitor, values, now, CancellationToken.None])!;

        // Verify: no clear event or clear call
        _alarmWriterMock.Verify(a => a.WriteHistoryAlarmAsync(
            It.IsAny<AlarmEvent>()), Times.Never);
        _alarmWriterMock.Verify(a => a.ClearRealtimeAlarmAsync(It.IsAny<string>()), Times.Never);
    }
}
