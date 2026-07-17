using Luculent.Sis.RuleEngine.Shared.Interfaces;
using Luculent.Sis.RuleEngine.Worker;
using Luculent.Sis.RuleEngine.Worker.Calculation;
using Luculent.Sis.RuleEngine.Worker.Calculation.Calculators;
using Luculent.Sis.RuleEngine.Worker.DataSource;
using Luculent.Sis.RuleEngine.Worker.Storage;

var builder = Host.CreateApplicationBuilder(args);

// ===== 数据源 =====
// 开发环境使用模拟数据，生产环境替换为 TrendDbReader
builder.Services.AddSingleton<ITrendDataReader, SimulatedTrendReader>();

// ===== 状态存储 =====
// 开发环境使用内存存储，生产环境替换为 RocksDbStateStore
builder.Services.AddSingleton<IStateStore, InMemoryStateStore>();

// ===== 报警写入 =====
// 根据环境变量选择存储后端:
//   REDIS_CONNECTION + CLICKHOUSE_CONNECTION → Redis + ClickHouse (生产)
//   未配置 → InMemory (开发/测试)
var redisConn = builder.Configuration.GetValue<string>("REDIS_CONNECTION");
var clickhouseConn = builder.Configuration.GetValue<string>("CLICKHOUSE_CONNECTION");

if (!string.IsNullOrEmpty(redisConn) && !string.IsNullOrEmpty(clickhouseConn))
{
    builder.Services.AddSingleton<IAlarmWriter>(sp =>
    {
        var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
        var redisWriter = new RedisAlarmWriter(redisConn, loggerFactory.CreateLogger<RedisAlarmWriter>());
        var clickhouseWriter = new ClickHouseAlarmWriter(clickhouseConn, loggerFactory.CreateLogger<ClickHouseAlarmWriter>());
        return new ProductionAlarmWriter(redisWriter, clickhouseWriter, loggerFactory.CreateLogger<ProductionAlarmWriter>());
    });
}
else
{
    builder.Services.AddSingleton<IAlarmWriter, InMemoryAlarmWriter>();
}

// ===== 规则计算器 (9 种) =====
builder.Services.AddSingleton<CalculateRuleExpression>();
builder.Services.AddSingleton<CalculateRuleRangeDuration>();
builder.Services.AddSingleton<CalculateRuleRangeFrequency>();
builder.Services.AddSingleton<CalculateRulePackageValue>();
builder.Services.AddSingleton<CalculateRuleMultiStateRangeDuration>();
builder.Services.AddSingleton<CalculateFeatureValue>();
builder.Services.AddSingleton<CalculatePackageValue>();
builder.Services.AddSingleton<CalculateWallTemperature>();
builder.Services.AddSingleton<CalculateInterfaceMonitoring>();

// ===== Master 配置客户端（多 Worker 部署时拉取配置） =====
var masterApiUrl = builder.Configuration.GetValue<string>("MASTER_API_URL");
if (!string.IsNullOrEmpty(masterApiUrl))
{
    builder.Services.AddHttpClient<MasterConfigClient>(client =>
    {
        client.BaseAddress = new Uri(masterApiUrl);
        client.Timeout = TimeSpan.FromSeconds(30);
    });
    builder.Services.AddSingleton<MasterConfigClient>();
}

// ===== 前置规则检查管道 =====
builder.Services.AddSingleton<PrerulePipeline>();

// ===== 规则分发器 =====
builder.Services.AddSingleton<RuleDispatcher>();

// ===== 主计算服务 =====
builder.Services.AddHostedService<WorkerCalculationService>();

var host = builder.Build();

// 多 Worker 模式: 从 Master 拉取初始配置（在 host.Run 之前）
if (!string.IsNullOrEmpty(masterApiUrl))
{
    var configClient = host.Services.GetRequiredService<MasterConfigClient>();
    var calcService = host.Services.GetRequiredService<WorkerCalculationService>();
    var logger = host.Services.GetRequiredService<ILogger<Program>>();

    try
    {
        var monitors = await configClient.FetchFullConfigAsync();
        foreach (var m in monitors)
            calcService.AssignedMonitors[m.Id] = m;
        logger.LogInformation("从 Master 拉取配置完成: {Count} 个监视项", monitors.Count);
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "从 Master 拉取配置失败，Worker 将以空配置启动");
    }
}

await host.RunAsync();
