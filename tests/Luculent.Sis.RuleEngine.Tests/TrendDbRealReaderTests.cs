using Luculent.Sis.RuleEngine.TrendDb;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit.Abstractions;

namespace Luculent.Sis.RuleEngine.Tests;

public class TrendDbRealReaderTest
{
    private readonly ITestOutputHelper _output;

    public TrendDbRealReaderTest(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task Read_Single_Tag_dbtest1()
    {
        var options = Options.Create(new TrendDbOptions
        {
            ConnectionString = "Type=TrendDB5;SERVER=10.181.0.59:20100;DATABASE=db05;UID=system;PWD=luculent123@",
            RealTimePoolSize = 1,
        });

        using var loggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Debug));

        var pool = new TrendDbConnectionPool(options, loggerFactory.CreateLogger<TrendDbConnectionPool>());
        _output.WriteLine($"Pool connected: {pool.IsConnected}");

        var reader = new TrendDbRealReader(pool, loggerFactory.CreateLogger<TrendDbRealReader>());
        _output.WriteLine($"IsConnected: {reader.IsConnected}");

        Assert.True(reader.IsConnected, "TrendDB 连接失败");

        // 读取 db05.test1
        var result = await reader.ReadBatchAsync(new[] { "db05.test1" });
        _output.WriteLine($"结果数量: {result.Count}");
        foreach (var kv in result)
            _output.WriteLine($"  {kv.Key} = {kv.Value?.ToString() ?? "null"}");
    }
}
