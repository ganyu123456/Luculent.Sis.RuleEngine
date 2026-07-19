using Luculent.Sis.RuleEngine.Shared.Interfaces;
using Luculent.Sis.RuleEngine.Worker.Storage;
using Microsoft.Extensions.Logging;
using Moq;

namespace Luculent.Sis.RuleEngine.Tests.Storage;

public class ProductionAlarmWriter_Tests
{
    private readonly Mock<IAlarmWriter> _realtimeMock;
    private readonly Mock<IAlarmWriter> _historyMock;
    private readonly ProductionAlarmWriter _writer;

    public ProductionAlarmWriter_Tests()
    {
        _realtimeMock = new Mock<IAlarmWriter>();
        _historyMock = new Mock<IAlarmWriter>();
        _writer = new ProductionAlarmWriter(
            _realtimeMock.Object,
            _historyMock.Object,
            Mock.Of<ILogger<ProductionAlarmWriter>>());
    }

    [Fact]
    public async Task GetLastEventStatuses_RedisCoversAll_ReturnsRedisResult()
    {
        var ids = new[] { "mon-1", "mon-2", "mon-3" };
        var redisResult = new Dictionary<string, string?>
        {
            ["mon-1"] = "satisfiled",
            ["mon-2"] = "",
            ["mon-3"] = "warning",
        };
        _realtimeMock.Setup(r => r.GetLastEventStatusesAsync(ids))
            .ReturnsAsync(redisResult);

        var result = await _writer.GetLastEventStatusesAsync(ids);

        Assert.Equal(3, result.Count);
        Assert.Equal("satisfiled", result["mon-1"]);
        Assert.Equal("", result["mon-2"]);
        Assert.Equal("warning", result["mon-3"]);
        _historyMock.Verify(
            h => h.GetLastEventStatusesAsync(It.IsAny<IEnumerable<string>>()),
            Times.Never);
    }

    [Fact]
    public async Task GetLastEventStatuses_RedisPartial_FallsBackToClickHouseForMissing()
    {
        var ids = new[] { "mon-1", "mon-2", "mon-3", "mon-4" };
        var redisResult = new Dictionary<string, string?>
        {
            ["mon-1"] = "satisfiled",
            ["mon-2"] = "",
        };
        _realtimeMock.Setup(r => r.GetLastEventStatusesAsync(ids))
            .ReturnsAsync(redisResult);

        var clickHouseResult = new Dictionary<string, string?>
        {
            ["mon-3"] = "warning",
            ["mon-4"] = "",
        };
        _historyMock.Setup(h => h.GetLastEventStatusesAsync(
                It.Is<IEnumerable<string>>(e => new HashSet<string>(e).SetEquals(
                    new[] { "mon-3", "mon-4" }))))
            .ReturnsAsync(clickHouseResult);

        var result = await _writer.GetLastEventStatusesAsync(ids);

        Assert.Equal(4, result.Count);
        Assert.Equal("satisfiled", result["mon-1"]);
        Assert.Equal("", result["mon-2"]);
        Assert.Equal("warning", result["mon-3"]);
        Assert.Equal("", result["mon-4"]);
    }

    [Fact]
    public async Task GetLastEventStatuses_RedisThrows_FallsBackToClickHouse()
    {
        var ids = new[] { "mon-1", "mon-2" };
        _realtimeMock.Setup(r => r.GetLastEventStatusesAsync(ids))
            .ThrowsAsync(new Exception("Redis connection refused"));

        var clickHouseResult = new Dictionary<string, string?>
        {
            ["mon-1"] = "satisfiled",
            ["mon-2"] = "",
        };
        _historyMock.Setup(h => h.GetLastEventStatusesAsync(ids))
            .ReturnsAsync(clickHouseResult);

        var result = await _writer.GetLastEventStatusesAsync(ids);

        Assert.Equal(2, result.Count);
        Assert.Equal("satisfiled", result["mon-1"]);
        Assert.Equal("", result["mon-2"]);
    }

    [Fact]
    public async Task GetLastEventStatuses_BothFail_ReturnsEmptyForAll()
    {
        var ids = new[] { "mon-1", "mon-2" };
        _realtimeMock.Setup(r => r.GetLastEventStatusesAsync(ids))
            .ThrowsAsync(new Exception("Redis down"));
        _historyMock.Setup(h => h.GetLastEventStatusesAsync(ids))
            .ThrowsAsync(new Exception("ClickHouse down"));

        var result = await _writer.GetLastEventStatusesAsync(ids);

        Assert.Equal(2, result.Count);
        Assert.Equal("", result["mon-1"]);
        Assert.Equal("", result["mon-2"]);
    }

    /// <summary>
    /// Redis 返回空字符串表示监视项处于正常态（上次事件为 clear）。
    /// 这是有效结果，不应触发 ClickHouse 回退。
    /// </summary>
    [Fact]
    public async Task GetLastEventStatuses_RedisReturnsEmptyString_NoClickHouseFallback()
    {
        var ids = new[] { "mon-1" };
        var redisResult = new Dictionary<string, string?> { ["mon-1"] = "" };
        _realtimeMock.Setup(r => r.GetLastEventStatusesAsync(ids))
            .ReturnsAsync(redisResult);

        var result = await _writer.GetLastEventStatusesAsync(ids);

        Assert.Equal("", result["mon-1"]);
        _historyMock.Verify(
            h => h.GetLastEventStatusesAsync(It.IsAny<IEnumerable<string>>()),
            Times.Never);
    }
}
