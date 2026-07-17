using Luculent.Sis.RuleEngine.Master.Services;
using Luculent.Sis.RuleEngine.Shared.Enums;
using Luculent.Sis.RuleEngine.Shared.Models;
using Microsoft.Extensions.Logging;
using Moq;

namespace Luculent.Sis.RuleEngine.Tests.Partition;

public class CostAwarePartitioner_Tests
{
    private readonly PartitionService _partitioner;

    public CostAwarePartitioner_Tests()
    {
        var logger = Mock.Of<ILogger<PartitionService>>();
        _partitioner = new PartitionService(logger);
    }

    [Fact]
    public void Partition_EmptyMonitors_ReturnsEmptyAssignments()
    {
        var workers = new List<WorkerInfo>
        {
            new() { WorkerId = "w1" },
            new() { WorkerId = "w2" },
        };

        var result = _partitioner.Partition(new List<MonitorConfig>(), workers);

        Assert.Empty(result.MonitorToWorker);
        Assert.Equal(2, result.WorkerAssignments.Count);
    }

    [Fact]
    public void Partition_SingleWorker_GetsAllMonitors()
    {
        var workers = new List<WorkerInfo>
        {
            new() { WorkerId = "w1" },
        };
        var monitors = new List<MonitorConfig>
        {
            new() { Id = "m1", RuleType = RuleType.Expression, RefreshIntervalSecond = 60 },
            new() { Id = "m2", RuleType = RuleType.RangeDuration, RefreshIntervalSecond = 30 },
            new() { Id = "m3", RuleType = RuleType.Expression, RefreshIntervalSecond = 10 },
        };

        var result = _partitioner.Partition(monitors, workers);

        Assert.Equal(3, result.MonitorToWorker.Count);
        Assert.All(result.MonitorToWorker.Values, v => Assert.Equal("w1", v));
    }

    [Fact]
    public void Partition_HeterogeneousLoad_BalancesCost()
    {
        var workers = new List<WorkerInfo>
        {
            new() { WorkerId = "w1" },
            new() { WorkerId = "w2" },
            new() { WorkerId = "w3" },
        };

        var monitors = new List<MonitorConfig>();
        for (int i = 0; i < 100; i++)
        {
            monitors.Add(new MonitorConfig
            {
                Id = $"m-light-{i}",
                RuleType = RuleType.Expression,
                RefreshIntervalSecond = 60,
            });
        }
        for (int i = 0; i < 20; i++)
        {
            monitors.Add(new MonitorConfig
            {
                Id = $"m-heavy-{i}",
                RuleType = RuleType.WallTemperatureValue,
                RefreshIntervalSecond = 10,
            });
        }

        var result = _partitioner.Partition(monitors, workers);

        Assert.Equal(120, result.MonitorToWorker.Count);

        // 验证每个 Worker 都分到了监控项
        foreach (var workerId in new[] { "w1", "w2", "w3" })
        {
            Assert.True(result.WorkerAssignments[workerId].Count > 0,
                $"Worker {workerId} 应该分到监控项");
        }
    }

    [Fact]
    public void CalculateCost_HeavyRuleWithHighFrequency_HigherCost()
    {
        var light = new MonitorConfig
        {
            RuleType = RuleType.Expression,
            RefreshIntervalSecond = 60,
        };
        var heavy = new MonitorConfig
        {
            RuleType = RuleType.WallTemperatureValue,
            RefreshIntervalSecond = 10,
        };

        var lightCost = PartitionService.CalculateCost(light);
        var heavyCost = PartitionService.CalculateCost(heavy);

        // 重量规则的每周期成本应该更高
        Assert.True(heavyCost > lightCost,
            $"heavy cost ({heavyCost}) should be > light cost ({lightCost})");
    }

    [Fact]
    public void CalculateCost_SameRuleType_FasterIntervalCostsMore()
    {
        var slow = new MonitorConfig
        {
            RuleType = RuleType.Expression,
            RefreshIntervalSecond = 300,
        };
        var fast = new MonitorConfig
        {
            RuleType = RuleType.Expression,
            RefreshIntervalSecond = 1,
        };

        var slowCost = PartitionService.CalculateCost(slow);
        var fastCost = PartitionService.CalculateCost(fast);

        Assert.True(fastCost > slowCost,
            $"fast cost ({fastCost}) should be > slow cost ({slowCost})");
    }

    [Fact]
    public void Partition_ThrowsOnEmptyWorkers()
    {
        Assert.Throws<ArgumentException>(() =>
            _partitioner.Partition(new List<MonitorConfig>(), new List<WorkerInfo>()));
    }
}
