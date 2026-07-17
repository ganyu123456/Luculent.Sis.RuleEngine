using Luculent.Sis.RuleEngine.Shared.Enums;
using Luculent.Sis.RuleEngine.Shared.Models;

namespace Luculent.Sis.RuleEngine.Master.Services;

/// <summary>
/// Cost-Aware 静态分区器。贪心装箱算法，将监控项按计算成本均匀分配到 Worker。
/// </summary>
public class PartitionService
{
    private readonly ILogger<PartitionService> _logger;

    /// <summary>
    /// 规则复杂度权重映射。与 MonitorCenter 的 CalculateRule* 实际耗时对应。
    /// </summary>
    public static readonly Dictionary<RuleType, double> ComplexityWeight = new()
    {
        [RuleType.Expression] = 1.0,
        [RuleType.RangeDuration] = 2.0,
        [RuleType.RangeFrequency] = 3.0,
        [RuleType.PackageValue] = 2.0,
        [RuleType.FeatureValue] = 2.0,
        [RuleType.WallTemperatureValue] = 5.0,
        [RuleType.InterfaceMonitoring] = 5.0,
        [RuleType.RulePackageValue] = 3.0,
        [RuleType.RuleMultiStateRangeDuration] = 4.0,
    };

    public PartitionService(ILogger<PartitionService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 执行贪心装箱分区。
    /// 返回：每个 Worker 分配到的监控项列表，以及 MonitorId → WorkerId 的映射。
    /// </summary>
    public PartitionResult Partition(List<MonitorConfig> monitors, List<WorkerInfo> workers)
    {
        if (workers.Count == 0)
            throw new ArgumentException("Worker 数量必须 > 0");

        // ① 计算每个监控项的成本并按成本降序排列
        var weightedItems = monitors
            .Select(m => new
            {
                Monitor = m,
                Cost = CalculateCost(m),
            })
            .OrderByDescending(x => x.Cost)
            .ToList();

        // ② 初始化 Worker bucket
        var buckets = workers.ToDictionary(
            w => w.WorkerId,
            w => new WorkerBucket { WorkerId = w.WorkerId });

        // ③ 贪心分配
        foreach (var weighted in weightedItems)
        {
            var lightest = buckets.Values.OrderBy(b => b.TotalCost).First();
            lightest.Monitors.Add(weighted.Monitor);
            lightest.TotalCost += weighted.Cost;
        }

        // ④ 构建结果
        var result = new PartitionResult
        {
            CalculatedAt = DateTime.UtcNow,
            ShardEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };

        foreach (var (workerId, bucket) in buckets)
        {
            result.WorkerAssignments[workerId] = bucket.Monitors;
            foreach (var m in bucket.Monitors)
                result.MonitorToWorker[m.Id] = workerId;

            _logger.LogInformation("Worker {WorkerId}: {Count} 监控项, 总成本 {Cost:F2}",
                workerId, bucket.Monitors.Count, bucket.TotalCost);
        }

        // ⑤ 计算均衡度
        var costs = buckets.Values.Select(b => b.TotalCost).ToList();
        var avg = costs.Average();
        var max = costs.Max();
        var imbalance = avg > 0 ? (max - avg) / avg * 100 : 0;
        _logger.LogInformation("分区完成: {WorkerCount} Worker, {MonitorCount} 监控项, 不均衡度 {Imbalance:F1}%",
            workers.Count, monitors.Count, imbalance);

        return result;
    }

    /// <summary>
    /// 计算单个监控项的计算成本。
    /// cost = 复杂度权重 × (1.0 / 刷新间隔)
    /// </summary>
    public static double CalculateCost(MonitorConfig monitor)
    {
        var weight = ComplexityWeight.TryGetValue(monitor.RuleType, out var w) ? w : 1.0;
        var frequency = 1.0 / Math.Max(monitor.RefreshIntervalSecond, 1);
        return weight * frequency;
    }
}

public class PartitionResult
{
    public Dictionary<string, List<MonitorConfig>> WorkerAssignments { get; set; } = new();
    public Dictionary<string, string> MonitorToWorker { get; set; } = new();
    public DateTime CalculatedAt { get; set; }
    public long ShardEpoch { get; set; }
}

public class WorkerBucket
{
    public string WorkerId { get; set; } = string.Empty;
    public List<MonitorConfig> Monitors { get; set; } = new();
    public double TotalCost { get; set; }
}

public class WorkerInfo
{
    public string WorkerId { get; set; } = string.Empty;
    public string GrpcAddress { get; set; } = string.Empty;
    public int Capacity { get; set; } = 100000;
    public int MonitorCount { get; set; }
    public DateTime RegisteredAt { get; set; }
    public DateTime LastHeartbeat { get; set; }
    public WorkerStatus Status { get; set; }
}

public enum WorkerStatus
{
    Online,
    Offline,
    Draining,
}
