using System.Collections.Concurrent;
using System.Reflection;
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

    private static readonly MethodInfo ComputeMonitorMethod =
        typeof(WorkerCalculationService).GetMethod("ComputeMonitor",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly Type TransitionType =
        typeof(WorkerCalculationService).GetNestedType("MonitorTransition",
            BindingFlags.NonPublic)!;

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

    /// <summary>
    /// 通过反射调用 ComputeMonitor，传入正确类型的 ConcurrentBag&lt;MonitorTransition&gt;。
    /// </summary>
    private object InvokeComputeMonitor(
        MonitorConfig monitor,
        IDictionary<string, double?> values,
        DateTime now,
        CalculationState? preloadedState,
        ConcurrentDictionary<string, CalculationState> modifiedStates)
    {
        var bagType = typeof(ConcurrentBag<>).MakeGenericType(TransitionType);
        var bag = Activator.CreateInstance(bagType)!;

        ComputeMonitorMethod.Invoke(_service, [monitor, values, now, preloadedState, modifiedStates, bag]);
        return bag;
    }

    private static int GetTransitionCount(object bag)
    {
        var prop = bag.GetType().GetProperty("Count")!;
        return (int)prop.GetValue(bag)!;
    }

    private static object? PeekTransition(object bag)
    {
        var tryPeek = bag.GetType().GetMethod("TryPeek")!;
        var args = new object?[] { null };
        var success = (bool)tryPeek.Invoke(bag, args)!;
        return success ? args[0] : null;
    }

    private static T? GetField<T>(object transition, string fieldName)
    {
        // record struct — try property first, then field
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        var prop = TransitionType.GetProperty(fieldName, flags);
        if (prop != null)
            return (T?)prop.GetValue(transition);

        var field = TransitionType.GetField(fieldName, flags);
        if (field != null)
            return (T?)field.GetValue(transition);

        return default;
    }

    [Fact]
    public void ComputeMonitor_StateChange_WritesTransition()
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

        var transitions = InvokeComputeMonitor(monitor, values, now, preloaded, modifiedStates);

        Assert.True(modifiedStates.TryGetValue(monitor.Id, out var saved));
        Assert.Equal("satisfiled", saved.PreviousStatus);
        Assert.Equal(1, GetTransitionCount(transitions));

        var t = PeekTransition(transitions);
        Assert.NotNull(t);
        Assert.Equal("satisfiled", GetField<string>(t, "NewStatus"));
    }

    [Fact]
    public void ComputeMonitor_SameStatus_NoTransition()
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

        var transitions = InvokeComputeMonitor(monitor, values, now, preloaded, modifiedStates);

        Assert.Equal(0, GetTransitionCount(transitions));
        Assert.False(modifiedStates.ContainsKey(monitor.Id));
    }

    [Fact]
    public void ComputeMonitor_ClearEvent_WritesEmptyStatusTransition()
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

        var transitions = InvokeComputeMonitor(monitor, values, now, preloaded, modifiedStates);

        Assert.True(modifiedStates.TryGetValue(monitor.Id, out var saved));
        Assert.Equal("", saved.PreviousStatus);
        Assert.Equal(1, GetTransitionCount(transitions));

        var t = PeekTransition(transitions);
        Assert.NotNull(t);
        Assert.Equal("", GetField<string>(t, "NewStatus"));
    }

    [Fact]
    public void ComputeMonitor_AfterClear_NoDuplicateTransition()
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

        var transitions = InvokeComputeMonitor(monitor, values, now, preloaded, modifiedStates);

        Assert.Equal(0, GetTransitionCount(transitions));
        Assert.False(modifiedStates.ContainsKey(monitor.Id));
    }

    [Fact]
    public void ComputeMonitor_PereruleSuppressWithClear_WritesEmptyStatusTransition()
    {
        var monitor = CreateMonitor();
        var now = DateTime.UtcNow;
        var values = new Dictionary<string, double?> { ["tag1"] = 0.0 };
        var preloaded = new CalculationState { MonitorId = monitor.Id, PreviousStatus = "satisfiled" };
        var modifiedStates = new ConcurrentDictionary<string, CalculationState>();

        _preruleMock.Setup(p => p.CheckAsync(monitor))
            .ReturnsAsync(new PreruleCheckResult { ShouldSuppress = true, ShouldClearAlarm = true });

        var transitions = InvokeComputeMonitor(monitor, values, now, preloaded, modifiedStates);

        Assert.True(modifiedStates.TryGetValue(monitor.Id, out var saved));
        Assert.Equal("", saved.PreviousStatus);
        Assert.Equal(1, GetTransitionCount(transitions));

        var t = PeekTransition(transitions);
        Assert.NotNull(t);
        Assert.Equal("", GetField<string>(t, "NewStatus"));
    }

    [Fact]
    public void ComputeMonitor_PereruleSuppressNoClear_NoTransition()
    {
        var monitor = CreateMonitor();
        var now = DateTime.UtcNow;
        var values = new Dictionary<string, double?> { ["tag1"] = 0.0 };
        var preloaded = new CalculationState { MonitorId = monitor.Id, PreviousStatus = "satisfiled" };
        var modifiedStates = new ConcurrentDictionary<string, CalculationState>();

        _preruleMock.Setup(p => p.CheckAsync(monitor))
            .ReturnsAsync(new PreruleCheckResult { ShouldSuppress = true, ShouldClearAlarm = false });

        var transitions = InvokeComputeMonitor(monitor, values, now, preloaded, modifiedStates);

        Assert.Equal(0, GetTransitionCount(transitions));
        Assert.False(modifiedStates.ContainsKey(monitor.Id));
    }

    [Fact]
    public void ComputeMonitor_FirstCalculation_NoSpuriousTransition()
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

        var transitions = InvokeComputeMonitor(monitor, values, now, preloaded, modifiedStates);

        Assert.Equal(0, GetTransitionCount(transitions));
    }

    // ===== F4: GetAllTagNames 缓存 =====

    [Fact]
    public void GetAllTagNames_ReturnsCachedResult_OnSecondCall()
    {
        _service.AssignedMonitors.Clear();
        _service.InvalidateTagNameCache();

        _service.AssignedMonitors["m1"] = new MonitorConfig
        {
            Id = "m1", TagName = "tag_a",
            RuleOptions = new MonitorRuleOptions(),
        };

        var first = _service.GetAllTagNames();
        var second = _service.GetAllTagNames();

        Assert.Same(first, second);
        Assert.Equal(new[] { "tag_a" }, first);
    }

    [Fact]
    public void GetAllTagNames_InvalidateCache_ReturnsNewResult()
    {
        _service.AssignedMonitors.Clear();
        _service.InvalidateTagNameCache();

        _service.AssignedMonitors["m1"] = new MonitorConfig
        {
            Id = "m1", TagName = "tag_a",
            RuleOptions = new MonitorRuleOptions(),
        };

        var first = _service.GetAllTagNames();

        _service.AssignedMonitors["m2"] = new MonitorConfig
        {
            Id = "m2", TagName = "tag_b",
            RuleOptions = new MonitorRuleOptions(),
        };
        _service.InvalidateTagNameCache();

        var second = _service.GetAllTagNames();

        Assert.NotSame(first, second);
        Assert.Contains("tag_a", second);
        Assert.Contains("tag_b", second);
    }

    [Fact]
    public void GetAllTagNames_IncludesRangeDurationTags()
    {
        _service.AssignedMonitors.Clear();
        _service.InvalidateTagNameCache();

        _service.AssignedMonitors["m1"] = new MonitorConfig
        {
            Id = "m1",
            TagName = "main_tag",
            RuleOptions = new MonitorRuleOptions
            {
                RangeDurationRules = new List<RangeDurationRuleConfig>
                {
                    new() { LeftTagName = "left_1", RightTagName = "right_1" },
                    new() { LeftTagName = "left_2", RightTagName = "right_2" },
                },
            },
        };

        var tags = _service.GetAllTagNames();

        Assert.Contains("main_tag", tags);
        Assert.Contains("left_1", tags);
        Assert.Contains("right_1", tags);
        Assert.Contains("left_2", tags);
        Assert.Contains("right_2", tags);
    }

    [Fact]
    public void GetAllTagNames_IncludesRangeFrequencyAndWallTempTags()
    {
        _service.AssignedMonitors.Clear();
        _service.InvalidateTagNameCache();

        _service.AssignedMonitors["m1"] = new MonitorConfig
        {
            Id = "m1",
            TagName = "main",
            RuleOptions = new MonitorRuleOptions
            {
                RangeFrequencyRules = new List<RangeFrequencyRuleConfig>
                {
                    new() { LeftTagName = "freq_left", RightTagName = "freq_right" },
                },
                WallTemperatureOpts = new WallTemperatureOptions
                {
                    TemperatureTag = "wall_temp",
                    ReferenceTag = "wall_ref",
                },
            },
        };

        var tags = _service.GetAllTagNames();

        Assert.Contains("main", tags);
        Assert.Contains("freq_left", tags);
        Assert.Contains("freq_right", tags);
        Assert.Contains("wall_temp", tags);
        Assert.Contains("wall_ref", tags);
    }

    [Fact]
    public void GetCurrentTagValue_PrefersRangeDurationLeftTag()
    {
        var monitor = new MonitorConfig
        {
            Id = "m1",
            TagName = "fallback",
            RuleOptions = new MonitorRuleOptions
            {
                RangeDurationRules = new List<RangeDurationRuleConfig>
                {
                    new() { LeftTagName = "primary_tag", RightTagName = "threshold" },
                },
            },
        };
        var values = new Dictionary<string, double?>
        {
            ["primary_tag"] = 88.5,
            ["fallback"] = 10.0,
        };

        // GetCurrentTagValue 是 private static，用反射调用
        var method = typeof(WorkerCalculationService).GetMethod("GetCurrentTagValue",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var result = (double)method.Invoke(null, [monitor, values])!;

        Assert.Equal(88.5, result);
    }
}
