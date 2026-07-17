using Luculent.Sis.RuleEngine.Master.Services;
using Luculent.Sis.RuleEngine.Shared.DTOs;
using Luculent.Sis.RuleEngine.Shared.Interfaces;
using Luculent.Sis.RuleEngine.Worker;
using Luculent.Sis.RuleEngine.Worker.Calculation;
using Luculent.Sis.RuleEngine.Worker.Calculation.Calculators;
using Luculent.Sis.RuleEngine.Worker.DataSource;
using Luculent.Sis.RuleEngine.Worker.Storage;

var builder = WebApplication.CreateBuilder(args);

// ===== Master 服务注册 =====
builder.Services.AddSingleton<ConfigurationService>();
builder.Services.AddSingleton<PartitionService>();
builder.Services.AddSingleton<WorkerManager>();
builder.Services.AddSingleton<AlarmQueryService>();
builder.Services.AddSingleton<HistoryAlarmService>();
builder.Services.AddSingleton<DashboardService>();
builder.Services.AddSingleton<PrerulePipeline>();

// ===== Worker 服务注册（内嵌运行） =====
builder.Services.AddSingleton<ITrendDataReader, SimulatedTrendReader>();
builder.Services.AddSingleton<IStateStore, InMemoryStateStore>();
builder.Services.AddSingleton<IAlarmWriter, InMemoryAlarmWriter>();

builder.Services.AddSingleton<CalculateRuleExpression>();
builder.Services.AddSingleton<CalculateRuleRangeDuration>();
builder.Services.AddSingleton<CalculateRuleRangeFrequency>();
builder.Services.AddSingleton<CalculateRulePackageValue>();
builder.Services.AddSingleton<CalculateRuleMultiStateRangeDuration>();
builder.Services.AddSingleton<CalculateFeatureValue>();
builder.Services.AddSingleton<CalculatePackageValue>();
builder.Services.AddSingleton<CalculateWallTemperature>();
builder.Services.AddSingleton<CalculateInterfaceMonitoring>();
builder.Services.AddSingleton<RuleDispatcher>();
builder.Services.AddSingleton<WorkerCalculationService>();

// Worker 计算循环作为 HostedService
builder.Services.AddHostedService(sp => sp.GetRequiredService<WorkerCalculationService>());

var app = builder.Build();

// ===== 配置同步 API =====
var syncGroup = app.MapGroup("/api/ruleengine/sync");

// 返回全量配置（供 Worker 拉取）
syncGroup.MapGet("/full/config", (ConfigurationService config) =>
{
    return Results.Ok(config.All.Values.ToList());
});

syncGroup.MapPost("/full", async (SyncFullRequest request,
    ConfigurationService config,
    PartitionService partition,
    WorkerManager workers,
    WorkerCalculationService calcService,
    ILogger<Program> logger) =>
{
    logger.LogInformation("全量同步: {Count} 个监视项, 版本 {Version}", request.Monitors.Count, request.Version);

    config.LoadFull(request.Monitors);

    // 将全量配置分配给 Worker 计算服务
    foreach (var m in request.Monitors)
    {
        calcService.AssignedMonitors[m.Id] = m;
    }

    var activeWorkers = workers.GetActiveWorkers();
    if (activeWorkers.Count > 0)
    {
        partition.Partition(request.Monitors, activeWorkers);
    }

    return Results.Ok(new SyncResponse
    {
        Success = true,
        WorkerCount = Math.Max(1, activeWorkers.Count),
        TotalMonitors = request.Monitors.Count,
    });
});

syncGroup.MapPost("/delta", (SyncDeltaRequest request,
    ConfigurationService config,
    WorkerCalculationService calcService,
    ILogger<Program> logger) =>
{
    logger.LogInformation("增量同步: +{Added} ~{Modified} -{Deleted}, 版本 {Version}",
        request.Added.Count, request.Modified.Count, request.Deleted.Count, request.Version);

    if (request.Added.Count > 0) config.Add(request.Added);
    if (request.Modified.Count > 0) config.Update(request.Modified);
    if (request.Deleted.Count > 0)
    {
        config.Remove(request.Deleted);
        foreach (var id in request.Deleted)
            calcService.AssignedMonitors.TryRemove(id, out _);
    }

    // 增量更新 Worker
    foreach (var m in request.Added)
        calcService.AssignedMonitors[m.Id] = m;
    foreach (var m in request.Modified)
        calcService.AssignedMonitors[m.Id] = m;

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

// 历史报警查询
alarmGroup.MapPost("/history", async (AlarmQueryRequest request, HistoryAlarmService historySvc) =>
{
    var result = await historySvc.QueryAsync(request);
    return Results.Ok(result);
});

alarmGroup.MapGet("/history/{monitorId}", async (string monitorId, HistoryAlarmService historySvc) =>
{
    var result = await historySvc.QueryByMonitorAsync(monitorId);
    return Results.Ok(result);
});

// 闭环验证: 检查是否有触发但无消除的报警事件
alarmGroup.MapGet("/history/closed-loop/validate", async (HistoryAlarmService historySvc) =>
{
    var now = DateTime.UtcNow;
    var startTime = now.AddDays(-7);
    var result = await historySvc.ValidateClosedLoopAsync(startTime, now);
    return Results.Ok(result);
});

// ===== Worker 注册 API =====
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

// ===== Worker 状态 API =====
app.MapGet("/api/ruleengine/worker/monitors/count", (WorkerCalculationService calcService) =>
{
    return Results.Ok(new
    {
        Count = calcService.AssignedMonitors.Count,
        Timestamp = DateTime.UtcNow,
    });
});

// ===== 健康检查 =====
app.MapGet("/api/ruleengine/health", (ConfigurationService config, WorkerManager workers, WorkerCalculationService calcService) =>
{
    return Results.Ok(new
    {
        Status = "healthy",
        MonitorCount = config.Count,
        AssignedCount = calcService.AssignedMonitors.Count,
        ActiveWorkers = workers.ActiveCount,
        Timestamp = DateTime.UtcNow,
    });
});

app.UseStaticFiles();

// ===== Dashboard =====
app.MapGet("/dashboard", () => Results.Redirect("/dashboard.html"));

app.MapGet("/api/ruleengine/dashboard/data", async (DashboardService dashboard) =>
{
    var data = await dashboard.GetDashboardDataAsync();
    return Results.Ok(data);
});

app.MapGet("/", () => "Luculent.Sis.RuleEngine Master + Worker (in-process)");

app.Run();
