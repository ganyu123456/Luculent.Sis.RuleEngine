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
// 开发环境使用内存存储，生产环境替换为 RedisAlarmWriter + ClickHouseAlarmWriter
builder.Services.AddSingleton<IAlarmWriter, InMemoryAlarmWriter>();

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

// ===== 规则分发器 =====
builder.Services.AddSingleton<RuleDispatcher>();

// ===== 主计算服务 =====
builder.Services.AddHostedService<WorkerCalculationService>();

var host = builder.Build();
host.Run();
