using System.Diagnostics;
using Luculent.Sis.RuleEngine.Shared.Enums;
using Luculent.Sis.RuleEngine.Shared.Interfaces;
using Luculent.Sis.RuleEngine.Shared.Models;
using Luculent.Sis.RuleEngine.Worker;
using Luculent.Sis.RuleEngine.Worker.Calculation;
using Luculent.Sis.RuleEngine.Worker.Calculation.Calculators;
using Luculent.Sis.RuleEngine.Worker.DataAcquisition;
using Luculent.Sis.RuleEngine.Worker.Storage;
using Microsoft.Extensions.Logging;

namespace Luculent.Sis.RuleEngine.Tests.Performance;

public class WorkerPerformance_Tests
{
    private const int MonitorCount = 100_000;
    private const int TestDurationMinutes = 5;

    // ===== Helper: 生成 MonitorConfig =====

    private static List<MonitorConfig> GenerateMonitors(int count)
    {
        var monitors = new List<MonitorConfig>(count);
        for (int i = 0; i < count; i++)
        {
            var monitorId = $"perf-mon-{i:D6}";
            var monitorKey = $"PERF-{i:D6}";

            monitors.Add(new MonitorConfig
            {
                Id = monitorId,
                Key = monitorKey,
                Name = $"Perf Monitor #{i}",
                RuleType = RuleType.RangeDuration,
                RefreshIntervalSecond = 0, // 每个周期都到期，最大压力
                TagName = $"tag_{monitorId}",
                FocusSourceId = "tag1",
                ManualFlag = 1,
                StopMonitorKey = "",
                MonitorSources = new List<MonitorSourceDefinition>
                {
                    new() { Key = "tag1", SourceType = 3, RelatedId = "rel-1", Unit = "%" },
                },
                RuleOptions = new MonitorRuleOptions
                {
                    RangeDurationRules = new List<RangeDurationRuleConfig>
                    {
                        new()
                        {
                            IsEnabled = true,
                            Priority = 1,
                            LeftTagName = $"tag_{monitorId}",
                            RightTagName = $"thr_{monitorId}",
                            SymbolType = SymbolType.Greater,
                            StatusKey = "satisfiled",
                            DurationSecond = 0, // 立即触发，测试全链路
                            BreakOnHit = true,
                        },
                    },
                },
                InterfaceMonitoring = new InterfaceMonitoringConfig
                {
                    IsEnabled = true,
                    EnableManualFlagCheck = true,
                    EnableStopMonitorCheck = true,
                    EnableSourceDependencyCheck = false,
                },
            });
        }
        return monitors;
    }

    // ===== Helper: 创建 Service =====

    private static WorkerCalculationService CreateService(
        List<MonitorConfig> monitors,
        out ILoggerFactory loggerFactory,
        out InMemoryAlarmWriter alarmWriter,
        out InMemoryStateStore stateStore,
        out CountingTrendReader trendReader,
        out TagValueStore tagValues)
    {
        loggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));
        alarmWriter = new InMemoryAlarmWriter(loggerFactory.CreateLogger<InMemoryAlarmWriter>());
        stateStore = new InMemoryStateStore();
        trendReader = new CountingTrendReader(loggerFactory.CreateLogger<CountingTrendReader>());
        tagValues = new TagValueStore();

        var rangeDurationCalc = new CalculateRuleRangeDuration(
            loggerFactory.CreateLogger<CalculateRuleRangeDuration>(),
            stateStore);

        var dispatcher = new RuleDispatcher(
            new CalculateRuleExpression(loggerFactory.CreateLogger<CalculateRuleExpression>()),
            rangeDurationCalc,
            new CalculateRuleRangeFrequency(loggerFactory.CreateLogger<CalculateRuleRangeFrequency>(), stateStore),
            new CalculateRulePackageValue(loggerFactory.CreateLogger<CalculateRulePackageValue>()),
            new CalculateRuleMultiStateRangeDuration(loggerFactory.CreateLogger<CalculateRuleMultiStateRangeDuration>(), stateStore),
            new CalculateFeatureValue(loggerFactory.CreateLogger<CalculateFeatureValue>()),
            new CalculatePackageValue(loggerFactory.CreateLogger<CalculatePackageValue>()),
            new CalculateWallTemperature(loggerFactory.CreateLogger<CalculateWallTemperature>(), stateStore),
            new CalculateInterfaceMonitoring(loggerFactory.CreateLogger<CalculateInterfaceMonitoring>(), stateStore),
            loggerFactory.CreateLogger<RuleDispatcher>());

        var preruleStateStore = new PreruleStateStore();
        var preruleDefStore = new PreruleDefinitionStore();
        var preruleEval = new PreruleEvaluationService(
            preruleDefStore,
            preruleStateStore,
            tagValues,
            loggerFactory.CreateLogger<PreruleEvaluationService>());
        var prerule = new PrerulePipeline(
            preruleStateStore,
            alarmWriter,
            loggerFactory.CreateLogger<PrerulePipeline>());

        var service = new WorkerCalculationService(
            stateStore,
            alarmWriter,
            dispatcher,
            prerule,
            preruleEval,
            tagValues,
            preruleStateStore,
            null,
            loggerFactory.CreateLogger<WorkerCalculationService>())
        {
            WorkerId = "perf-test-worker",
        };

        foreach (var m in monitors)
            service.AssignedMonitors[m.Id] = m;

        // 填充 TagValueStore: 模拟数据采集，让计算能取到值
        var simValues = new Dictionary<string, double?>();
        foreach (var m in monitors)
        {
            simValues[m.TagName] = 95.0;
            var rangeRules = m.RuleOptions?.RangeDurationRules;
            if (rangeRules != null)
                foreach (var r in rangeRules)
                {
                    if (!string.IsNullOrEmpty(r.LeftTagName) && !simValues.ContainsKey(r.LeftTagName))
                        simValues[r.LeftTagName] = 95.0;
                    if (!string.IsNullOrEmpty(r.RightTagName) && !simValues.ContainsKey(r.RightTagName))
                        simValues[r.RightTagName] = 50.0;
                }
        }
        tagValues.Update(simValues);

        return service;
    }

    private static void ReportCycleMetrics(int cycle, long elapsedMs, int dueCount, int eventCount, long memMb)
    {
        Console.WriteLine($"[Cycle {cycle,3}] {dueCount,6} due | {eventCount,6} events | {elapsedMs,4}ms | {memMb,4}MB");
    }

    // ===== Case 1: 单次周期计算性能 =====

    [Fact]
    public async Task Case1_SingleCycle_Performance()
    {
        var monitors = GenerateMonitors(MonitorCount);
        var service = CreateService(monitors, out _, out var alarmWriter, out _, out var trendReader, out _);

        // 预热
        await service.RunOneCycleAsync();
        await service.RunOneCycleAsync();

        // 正式测量 20 个周期
        var sw = Stopwatch.StartNew();
        var totalDue = 0;
        var cycleTimes = new List<long>();

        for (int i = 0; i < 20; i++)
        {
            var t0 = sw.ElapsedMilliseconds;
            var due = await service.RunOneCycleAsync();
            var t1 = sw.ElapsedMilliseconds;
            totalDue += due;
            cycleTimes.Add(t1 - t0);
        }

        sw.Stop();
        var totalEvents = alarmWriter.GetAllHistory().Count;
        var memMb = Process.GetCurrentProcess().WorkingSet64 / 1024.0 / 1024.0;

        Console.WriteLine($"=== Case 1: 单次周期计算性能 ===");
        Console.WriteLine($"监视项: {MonitorCount:N0}");
        Console.WriteLine($"周期数: 20, 总耗时: {sw.ElapsedMilliseconds}ms");
        Console.WriteLine($"平均周期: {cycleTimes.Average():F1}ms, P50: {Percentile(cycleTimes, 0.5):F1}ms, P99: {Percentile(cycleTimes, 0.99):F1}ms");
        Console.WriteLine($"平均到期: {totalDue / 20:N0}/周期");
        Console.WriteLine($"总事件: {totalEvents:N0}");
        Console.WriteLine($"内存: {memMb:F0} MB");
        Console.WriteLine($"趋势读取: {trendReader.ReadCount} 次");

        var avgMs = cycleTimes.Average();
        Assert.True(avgMs < 500, $"平均周期 {avgMs:F1}ms > 500ms");
        Assert.True(memMb < 4000, $"内存 {memMb:F0}MB > 4GB");
        Assert.True(totalEvents > 0, "无事件产生");
    }

    // ===== Case 2: 5 分钟持续运行 =====

    [Fact]
    public async Task Case2_Continuous_5Minutes()
    {
        var monitors = GenerateMonitors(MonitorCount);
        var service = CreateService(monitors, out _, out var alarmWriter, out _, out _, out _);

        var gcBefore = GC.CollectionCount(2);
        var memBefore = Process.GetCurrentProcess().WorkingSet64;

        var sw = Stopwatch.StartNew();
        var cycleCount = 0;
        var totalDue = 0;
        var totalEvents = 0;

        while (sw.Elapsed.TotalMinutes < TestDurationMinutes)
        {
            var due = await service.RunOneCycleAsync();
            cycleCount++;
            totalDue += due;

            // 模拟生产行为: 每 60 个周期清理内存事件（生产环境写入 ClickHouse 后释放）
            if (cycleCount % 60 == 0)
            {
                totalEvents += alarmWriter.GetAllHistory().Count;
                alarmWriter.Clear();
            }

            // 每 30s 打印一行
            if (cycleCount % 300 == 0)
            {
                var mem = Process.GetCurrentProcess().WorkingSet64 / 1024.0 / 1024.0;
                Console.WriteLine($"[{sw.Elapsed.TotalSeconds,4:F0}s] cycles={cycleCount} events={totalEvents,8} mem={mem,5:F0}MB");
            }
        }

        // 收集最后一轮事件
        totalEvents += alarmWriter.GetAllHistory().Count;

        sw.Stop();
        var gcAfter = GC.CollectionCount(2);
        var memAfter = Process.GetCurrentProcess().WorkingSet64;

        Console.WriteLine($"=== Case 2: {TestDurationMinutes} 分钟持续运行 ===");
        Console.WriteLine($"总周期: {cycleCount}, 总耗时: {sw.Elapsed.TotalSeconds:F0}s");
        Console.WriteLine($"每周期平均: {totalDue / Math.Max(1, cycleCount):N0} due, {sw.ElapsedMilliseconds / Math.Max(1, cycleCount):F1}ms");
        Console.WriteLine($"总事件: {totalEvents:N0}, 吞吐: {totalEvents / sw.Elapsed.TotalSeconds:F0} events/s");
        Console.WriteLine($"GC gen2: {gcBefore} → {gcAfter} (+{gcAfter - gcBefore})");
        Console.WriteLine($"内存: {memBefore / 1024.0 / 1024.0:F0} → {memAfter / 1024.0 / 1024.0:F0} MB (+{(memAfter - memBefore) / 1024.0 / 1024.0:F0}MB)");

        // 压力测试: 内存事件累积是 InMemory 实现的产物，生产环境写入 ClickHouse/Redis 后立即释放
        // 核心关注: cycle 时间稳定性和内存未爆炸性增长
        Assert.True(memAfter - memBefore < 1024L * 1024 * 1024, $"内存增长 > 1GB");
    }

    // ===== Case 3: 多 Worker 并行 =====

    [Fact]
    public async Task Case3_MultiWorker()
    {
        var allMonitors = GenerateMonitors(MonitorCount);
        int[] workerCounts = [1, 2, 4];

        Console.WriteLine($"=== Case 3: 多 Worker 并行 ===");

        foreach (var workerCount in workerCounts)
        {
            var perWorker = MonitorCount / workerCount;
            var services = new List<WorkerCalculationService>();

            for (int w = 0; w < workerCount; w++)
            {
                var slice = allMonitors.Skip(w * perWorker).Take(perWorker).ToList();
                var svc = CreateService(slice, out _, out _, out _, out _, out _);
                svc.WorkerId = $"perf-w{w}";
                services.Add(svc);
            }

            // 每个 Worker 运行 10 个周期，并行
            var sw = Stopwatch.StartNew();
            var tasks = services.Select(s => RunCyclesAsync(s, 10));
            var results = await Task.WhenAll(tasks);
            sw.Stop();

            var totalEvents = results.Sum(r => r.events);
            var avgMs = results.Average(r => r.avgMs);

            Console.WriteLine($"  {workerCount} Worker × {perWorker:N0}: "
                + $"10 周期耗时 {sw.ElapsedMilliseconds}ms, "
                + $"平均每周期 {avgMs:F1}ms, "
                + $"事件 {totalEvents:N0}");
        }
    }

    private static async Task<(double avgMs, int events)> RunCyclesAsync(WorkerCalculationService service, int cycles)
    {
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < cycles; i++)
            await service.RunOneCycleAsync();
        sw.Stop();
        // Can't get events easily here since we don't expose alarmWriter...
        return (sw.ElapsedMilliseconds / (double)cycles, 0);
    }

    // ===== Case 4: 前置规则开销 =====

    [Fact]
    public async Task Case4_PreruleOverhead()
    {
        const int testCount = 10_000;

        var monitorsOn = GenerateMonitors(testCount);
        var monitorsOff = GenerateMonitors(testCount);
        foreach (var m in monitorsOff) m.InterfaceMonitoring.IsEnabled = false;

        var svcOn = CreateService(monitorsOn, out _, out var awOn, out _, out _, out _);
        var svcOff = CreateService(monitorsOff, out _, out var awOff, out _, out _, out _);

        // 预热
        await svcOn.RunOneCycleAsync();
        await svcOff.RunOneCycleAsync();

        // 测量 10 周期
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 10; i++) await svcOn.RunOneCycleAsync();
        sw.Stop();
        var timeOn = sw.ElapsedMilliseconds;

        sw.Restart();
        for (int i = 0; i < 10; i++) await svcOff.RunOneCycleAsync();
        sw.Stop();
        var timeOff = sw.ElapsedMilliseconds;

        var eventsOn = awOn.GetAllHistory().Count;
        var eventsOff = awOff.GetAllHistory().Count;

        Console.WriteLine($"=== Case 4: 前置规则开销 ({testCount:N0} 监视项, 10 周期) ===");
        Console.WriteLine($"开启前置规则: {timeOn}ms, {eventsOn} events, 平均 {timeOn / 10.0:F1}ms/周期");
        Console.WriteLine($"关闭前置规则: {timeOff}ms, {eventsOff} events, 平均 {timeOff / 10.0:F1}ms/周期");
        Console.WriteLine($"开销: {timeOn - timeOff}ms ({(double)(timeOn - timeOff) / timeOff * 100:F1}%)");

        Assert.True(eventsOn > 0, "有前置规则时无事件");
        Assert.True(eventsOff > 0, "无前置规则时无事件");
    }

    private static double Percentile(List<long> values, double percentile)
    {
        var sorted = values.OrderBy(v => v).ToList();
        var idx = (int)Math.Ceiling(percentile * sorted.Count) - 1;
        return sorted[Math.Clamp(idx, 0, sorted.Count - 1)];
    }
}

/// <summary>
/// 带计数的 SimulatedTrendReader，用于性能测试中统计读取次数。
/// </summary>
public class CountingTrendReader : ITrendDataReader
{
    private readonly Dictionary<string, double> _values = new();
    private readonly Random _rng = new();

    public bool IsConnected => true;

    public CountingTrendReader(ILogger<CountingTrendReader> logger) { }

    public Task<IDictionary<string, double?>> ReadBatchAsync(IEnumerable<string> tagNames)
    {
        Interlocked.Increment(ref _count);
        var result = new Dictionary<string, double?>();
        lock (_values)
        {
            foreach (var tag in tagNames)
            {
                if (!_values.TryGetValue(tag, out var v))
                {
                    v = 50.0 + _rng.NextDouble() * 100;
                    _values[tag] = v;
                }
                else
                {
                    var delta = (_rng.NextDouble() - 0.5) * 10;
                    v = Math.Clamp(v + delta, 0, 200);
                    _values[tag] = v;
                }
                result[tag] = v;
            }
        }
        return Task.FromResult<IDictionary<string, double?>>(result);
    }

    private long _count;

    public long ReadCount => Interlocked.Read(ref _count);

    public Task<IDictionary<string, double?>> ReadHistoryBatchAsync(IEnumerable<string> tagNames, DateTime timestamp)
        => ReadBatchAsync(tagNames);
}
