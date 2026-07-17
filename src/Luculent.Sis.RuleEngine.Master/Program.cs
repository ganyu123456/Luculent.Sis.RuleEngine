using Luculent.Sis.RuleEngine.Master.Services;
using Luculent.Sis.RuleEngine.Shared.DTOs;
using Luculent.Sis.RuleEngine.Shared.Interfaces;
using Luculent.Sis.RuleEngine.Worker.Storage;

var builder = WebApplication.CreateBuilder(args);

// ===== Master 服务注册 =====
builder.Services.AddSingleton<ConfigurationService>();
builder.Services.AddSingleton<PartitionService>();
builder.Services.AddSingleton<WorkerManager>();
builder.Services.AddSingleton<AlarmQueryService>();

// 开发环境使用内存报警存储（Worker 和 Master 共享）
builder.Services.AddSingleton<IAlarmWriter, InMemoryAlarmWriter>();

// ===== 健康检查 =====
builder.Services.AddHealthChecks();

var app = builder.Build();

// ===== 配置同步 API =====
var syncGroup = app.MapGroup("/api/ruleengine/sync");

syncGroup.MapPost("/full", async (SyncFullRequest request, ConfigurationService config, PartitionService partition, WorkerManager workers, ILogger<Program> logger) =>
{
    logger.LogInformation("全量同步: {Count} 个监视项, 版本 {Version}", request.Monitors.Count, request.Version);

    config.LoadFull(request.Monitors);

    var activeWorkers = workers.GetActiveWorkers();
    if (activeWorkers.Count > 0)
    {
        var result = partition.Partition(request.Monitors, activeWorkers);
        return Results.Ok(new SyncResponse
        {
            Success = true,
            WorkerCount = activeWorkers.Count,
            TotalMonitors = request.Monitors.Count,
        });
    }

    return Results.Ok(new SyncResponse
    {
        Success = true,
        WorkerCount = 0,
        TotalMonitors = request.Monitors.Count,
        Error = "没有活跃的 Worker",
    });
});

syncGroup.MapPost("/delta", (SyncDeltaRequest request, ConfigurationService config, ILogger<Program> logger) =>
{
    logger.LogInformation("增量同步: +{Added} ~{Modified} -{Deleted}, 版本 {Version}",
        request.Added.Count, request.Modified.Count, request.Deleted.Count, request.Version);

    if (request.Added.Count > 0) config.Add(request.Added);
    if (request.Modified.Count > 0) config.Update(request.Modified);
    if (request.Deleted.Count > 0) config.Remove(request.Deleted);

    return Results.Ok(new SyncResponse { Success = true, TotalMonitors = config.Count });
});

// ===== 实时报警查询 API =====
var alarmGroup = app.MapGroup("/api/ruleengine/alarms");

alarmGroup.MapGet("/realtime", async (AlarmQueryService alarmQuery) =>
{
    var alarms = await alarmQuery.GetRealtimeAlarmsAsync();
    return Results.Ok(alarms);
});

alarmGroup.MapGet("/realtime/{monitorId}", async (string monitorId, AlarmQueryService alarmQuery) =>
{
    var alarm = await alarmQuery.GetRealtimeAlarmAsync(monitorId);
    return alarm != null ? Results.Ok(alarm) : Results.NotFound();
});

// ===== Worker 注册 API (gRPC 替代，开发环境使用 HTTP) =====
var workerGroup = app.MapGroup("/api/ruleengine/workers");

workerGroup.MapPost("/register", async (WorkerInfo worker, WorkerManager workers) =>
{
    var id = await workers.RegisterAsync(worker);
    return Results.Ok(new { workerId = id });
});

workerGroup.MapPost("/{workerId}/heartbeat", async (string workerId, WorkerManager workers) =>
{
    await workers.HeartbeatAsync(workerId);
    return Results.Ok();
});

// ===== 健康检查 =====
app.MapGet("/api/ruleengine/health", (ConfigurationService config, WorkerManager workers) =>
{
    return Results.Ok(new
    {
        Status = "healthy",
        MonitorCount = config.Count,
        ActiveWorkers = workers.ActiveCount,
        Timestamp = DateTime.UtcNow,
    });
});

app.MapGet("/", () => "Luculent.Sis.RuleEngine Master");

app.Run();
