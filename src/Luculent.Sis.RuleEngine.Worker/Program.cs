using Luculent.Sis.RuleEngine.Shared.Interfaces;
using Luculent.Sis.RuleEngine.Shared.Models;
using Luculent.Sis.RuleEngine.Worker;
using Luculent.Sis.RuleEngine.Worker.Calculation;
using Luculent.Sis.RuleEngine.Worker.Calculation.Calculators;
using Luculent.Sis.RuleEngine.Worker.DataSource;
using Luculent.Sis.RuleEngine.Worker.Services;
using Luculent.Sis.RuleEngine.Worker.Storage;

var builder = Host.CreateApplicationBuilder(args);

// Worker 标识: 环境变量 > MachineName
var workerId = builder.Configuration.GetValue<string>("WORKER_ID") ?? Environment.MachineName;

// ===== 数据源 =====
var trendDbConn = builder.Configuration.GetValue<string>("TRENDDB_CONNECTION");
if (!string.IsNullOrEmpty(trendDbConn))
{
    builder.Services.AddTrendDb(builder.Configuration);
}
else
{
    builder.Services.AddSingleton<ITrendDataReader, SimulatedTrendReader>();
}

// ===== 状态存储 =====
builder.Services.AddSingleton<IStateStore, InMemoryStateStore>();

// ===== 报警写入 =====
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

// ===== 规则计算器 =====
builder.Services.AddSingleton<CalculateRuleExpression>();
builder.Services.AddSingleton<CalculateRuleRangeDuration>();
builder.Services.AddSingleton<CalculateRuleRangeFrequency>();
builder.Services.AddSingleton<CalculateRulePackageValue>();
builder.Services.AddSingleton<CalculateRuleMultiStateRangeDuration>();
builder.Services.AddSingleton<CalculateFeatureValue>();
builder.Services.AddSingleton<CalculatePackageValue>();
builder.Services.AddSingleton<CalculateWallTemperature>();
builder.Services.AddSingleton<CalculateInterfaceMonitoring>();

// ===== 前置规则检查管道 =====
builder.Services.AddSingleton<PrerulePipeline>();
builder.Services.AddSingleton<IPrerulePipeline>(sp => sp.GetRequiredService<PrerulePipeline>());

// ===== 规则分发器 =====
builder.Services.AddSingleton<RuleDispatcher>();
builder.Services.AddSingleton<IRuleDispatcher>(sp => sp.GetRequiredService<RuleDispatcher>());

// ===== gRPC 连接服务 (Worker ↔ Master) =====
builder.Services.AddSingleton<GrpcConnectionService>();

// ===== Master HTTP 客户端 (fallback, gRPC 不可用时使用) =====
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

// ===== 主计算服务 =====
builder.Services.AddSingleton<WorkerCalculationService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<WorkerCalculationService>());

var host = builder.Build();

var calcService = host.Services.GetRequiredService<WorkerCalculationService>();
calcService.WorkerId = workerId;

// ===== gRPC 连接 Master (主通道, 启动后台运行) =====
var grpcService = host.Services.GetRequiredService<GrpcConnectionService>();
var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Luculent.Sis.RuleEngine.Worker");

_ = Task.Run(async () =>
{
    // 短暂的初始延迟，让 WorkerCalculationService 先启动
    await Task.Delay(TimeSpan.FromSeconds(2));

    using var appCts = new CancellationTokenSource();
    try
    {
        await grpcService.RunAsync(appCts.Token);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "gRPC 连接服务异常退出");
    }
});

await host.RunAsync();
