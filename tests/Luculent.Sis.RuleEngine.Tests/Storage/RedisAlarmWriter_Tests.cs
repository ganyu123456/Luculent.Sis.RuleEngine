using Luculent.Sis.RuleEngine.Worker.Storage;
using Microsoft.Extensions.Logging;
using Moq;
using StackExchange.Redis;

namespace Luculent.Sis.RuleEngine.Tests.Storage;

public class RedisAlarmWriter_Tests
{
    private readonly Mock<IDatabase> _dbMock;
    private readonly RedisAlarmWriter _writer;

    private const string ActiveSetKey = "ruleengine:active_alarms";
    private const string HashPrefix = "ruleengine:alarm:";

    public RedisAlarmWriter_Tests()
    {
        _dbMock = new Mock<IDatabase>();
        _writer = new RedisAlarmWriter(
            _dbMock.Object,
            Mock.Of<ILogger<RedisAlarmWriter>>());
    }

    [Fact]
    public async Task GetLastEventStatuses_EmptyInput_ReturnsEmptyDict()
    {
        var result = await _writer.GetLastEventStatusesAsync(Array.Empty<string>());

        Assert.Empty(result);
        _dbMock.Verify(d => d.SetMembersAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()), Times.Never);
    }

    [Fact]
    public async Task GetLastEventStatuses_HashHasValue_ReturnsStatusKey()
    {
        var ids = new[] { "mon-1", "mon-2" };
        SetupSetMembers(new[] { "mon-1", "mon-2" });
        SetupBatch(new Dictionary<string, RedisValue>
        {
            ["mon-1"] = new("satisfiled"),
            ["mon-2"] = new("warning"),
        });

        var result = await _writer.GetLastEventStatusesAsync(ids);

        Assert.Equal(2, result.Count);
        Assert.Equal("satisfiled", result["mon-1"]);
        Assert.Equal("warning", result["mon-2"]);
    }

    [Fact]
    public async Task GetLastEventStatuses_HashExpired_ExcludesAndCleansSet()
    {
        var ids = new[] { "mon-1", "mon-2" };
        SetupSetMembers(new[] { "mon-1", "mon-2" });
        SetupBatch(new Dictionary<string, RedisValue>
        {
            ["mon-1"] = new("satisfiled"),
            ["mon-2"] = RedisValue.Null, // Hash 过期
        });

        var result = await _writer.GetLastEventStatusesAsync(ids);

        // mon-2 过期 → 不纳入结果
        Assert.Single(result);
        Assert.Equal("satisfiled", result["mon-1"]);

        // 验证残留 SET 条目被清理
        _dbMock.Verify(d => d.SetRemoveAsync(
                ActiveSetKey, "mon-2", It.IsAny<CommandFlags>()),
            Times.Once);
    }

    [Fact]
    public async Task GetLastEventStatuses_NotInActiveSet_ReturnsEmptyString()
    {
        var ids = new[] { "mon-1", "mon-2" };
        // mon-2 不在活跃 SET 中
        SetupSetMembers(new[] { "mon-1" });
        SetupBatch(new Dictionary<string, RedisValue>
        {
            ["mon-1"] = new("satisfiled"),
        });

        var result = await _writer.GetLastEventStatusesAsync(ids);

        Assert.Equal(2, result.Count);
        Assert.Equal("satisfiled", result["mon-1"]);
        Assert.Equal("", result["mon-2"]); // 不在 SET → 正常态
    }

    [Fact]
    public async Task GetLastEventStatuses_NoneInActiveSet_ReturnsEmptyForAll()
    {
        var ids = new[] { "mon-1", "mon-2" };
        SetupSetMembers(Array.Empty<string>());

        var result = await _writer.GetLastEventStatusesAsync(ids);

        Assert.Equal(2, result.Count);
        Assert.Equal("", result["mon-1"]);
        Assert.Equal("", result["mon-2"]);
        // 没有活跃的 → 不创建 batch
        _dbMock.Verify(d => d.CreateBatch(It.IsAny<object>()), Times.Never);
    }

    [Fact]
    public async Task GetLastEventStatuses_SmembersThrows_ReturnsEmptyForAll()
    {
        var ids = new[] { "mon-1", "mon-2" };
        _dbMock.Setup(d => d.SetMembersAsync(ActiveSetKey, It.IsAny<CommandFlags>()))
            .ThrowsAsync(new Exception("Redis connection lost"));

        var result = await _writer.GetLastEventStatusesAsync(ids);

        Assert.Equal(2, result.Count);
        Assert.Equal("", result["mon-1"]);
        Assert.Equal("", result["mon-2"]);
    }

    private void SetupSetMembers(string[] activeIds)
    {
        _dbMock.Setup(d => d.SetMembersAsync(ActiveSetKey, It.IsAny<CommandFlags>()))
            .ReturnsAsync(activeIds.Select(id => (RedisValue)id).ToArray());
    }

    private void SetupBatch(Dictionary<string, RedisValue> keyValues)
    {
        var batchMock = new Mock<IBatch>();
        var tasks = new List<Task<RedisValue>>();

        foreach (var (monitorId, value) in keyValues)
        {
            var tcs = new TaskCompletionSource<RedisValue>();
            tcs.SetResult(value);
            var task = tcs.Task;
            tasks.Add(task);

            batchMock.Setup(b => b.HashGetAsync(
                    HashPrefix + monitorId, "status_key", It.IsAny<CommandFlags>()))
                .Returns(task);
        }

        batchMock.Setup(b => b.Execute());
        _dbMock.Setup(d => d.CreateBatch(It.IsAny<object>())).Returns(batchMock.Object);
    }
}
