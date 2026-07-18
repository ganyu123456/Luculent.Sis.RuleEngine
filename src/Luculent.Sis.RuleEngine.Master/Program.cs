using Luculent.Sis.RuleEngine.Master.Services;
using Luculent.Sis.RuleEngine.Shared.DTOs;
using Luculent.Sis.RuleEngine.Shared.Interfaces;
using Luculent.Sis.RuleEngine.Shared.Models;
using Luculent.Sis.RuleEngine.Worker;
using Luculent.Sis.RuleEngine.Worker.Calculation;
using Luculent.Sis.RuleEngine.Worker.Calculation.Calculators;
using Luculent.Sis.RuleEngine.Worker.DataSource;
using Luculent.Sis.RuleEngine.Worker.Storage;

var builder = WebApplication.CreateBuilder(args);

// gRPC 需要 HTTP/2
builder.WebHost.ConfigureKestrel(opts =>
{
    opts.ListenAnyIP(11082, listenOpts => listenOpts.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1);
    opts.ListenAnyIP(11083, listenOpts => listenOpts.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2);
});

builder.Services.AddGrpc();

// ===== Master 服务注册 =====
builder.Services.AddSingleton<ConfigurationService>();
builder.Services.AddSingleton<PartitionService>();
builder.Services.AddSingleton<WorkerManager>();
builder.Services.AddSingleton<GrpcConnectionService>();
builder.Services.AddSingleton<AlarmQueryService>();
builder.Services.AddSingleton<HistoryAlarmService>();
builder.Services.AddSingleton<DashboardService>();
builder.Services.AddSingleton<PreruleStateStore>();
builder.Services.AddSingleton<PreruleDefinitionStore>();
builder.Services.AddSingleton<PreruleDatabaseReader>();

// ===== Monitor Center 集成 =====
var monitorCenterUrl = builder.Configuration.GetValue<string>("MonitorCenter:ApiUrl");
if (!string.IsNullOrEmpty(monitorCenterUrl))
{
    builder.Services.AddHttpClient<MonitorCenterClient>(client =>
    {
        client.BaseAddress = new Uri(monitorCenterUrl);
        client.Timeout = TimeSpan.FromSeconds(30);
    });
}

// ===== Worker 依赖（仅用于 IAlarmWriter 查询，不启动计算循环）=====
var trendDbConn = builder.Configuration.GetValue<string>("TRENDDB_CONNECTION");
if (!string.IsNullOrEmpty(trendDbConn))
{
    builder.Services.AddTrendDb(builder.Configuration);
}
else
{
    builder.Services.AddSingleton<ITrendDataReader, SimulatedTrendReader>();
}
builder.Services.AddSingleton<IStateStore, InMemoryStateStore>();

// 报警写入: 跟 Worker 一致，读取环境变量选择后端
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

// 注意: 不注册 WorkerCalculationService 为 HostedService
// Master 是 Control Plane，不执行规则计算

var app = builder.Build();

// ===== 启动时从 Monitor Center 拉取全量监视项（后台执行，不阻塞启动） =====
if (!string.IsNullOrEmpty(monitorCenterUrl))
{
    _ = Task.Run(async () =>
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("Luculent.Sis.RuleEngine.Master");
        try
        {
            var client = app.Services.GetRequiredService<MonitorCenterClient>();
            var config = app.Services.GetRequiredService<ConfigurationService>();
            var partition = app.Services.GetRequiredService<PartitionService>();
            var workers = app.Services.GetRequiredService<WorkerManager>();

            // 等待 Worker 注册后再拉取配置
            for (int i = 0; i < 12 && workers.ActiveCount == 0; i++)
            {
                logger.LogInformation("等待 Worker 注册... ({Retry}/12)", i + 1);
                await Task.Delay(5000);
            }

            var monitors = await client.FetchAllMonitorsAsync();
            logger.LogInformation("从 Monitor Center 拉取到 {Count} 个监视项", monitors.Count);

            if (monitors.Count > 0)
            {
                config.LoadFull(monitors);

                var activeWorkers = workers.GetActiveWorkers();
                if (activeWorkers.Count > 0)
                {
                    var result = partition.Partition(monitors, activeWorkers);
                    config.SetWorkerAssignments(result.WorkerAssignments);
                    logger.LogInformation("启动分区完成: {WorkerCount} Worker", activeWorkers.Count);

                    // 通过 gRPC 推送配置到各 Worker
                    var grpcService = app.Services.GetRequiredService<GrpcConnectionService>();
                    await grpcService.PushToWorkersAsync(result.WorkerAssignments);
                }
            }

            // 拉取前置规则定义
            try
            {
                var prerules = await client.FetchAllPrerulesAsync();
                if (prerules.Count > 0)
                {
                    var preruleStore = app.Services.GetRequiredService<PreruleDefinitionStore>();
                    preruleStore.LoadAll(prerules);
                    logger.LogInformation("启动加载前置规则: {Count} 条 (来自 MonitorCenter)", prerules.Count);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "从 Monitor Center 拉取前置规则失败，尝试数据库 fallback");
            }

            // Fallback: 如果未加载任何前置规则，尝试从数据库直读
            var store = app.Services.GetRequiredService<PreruleDefinitionStore>();
            if (store.GetAll().Count == 0)
            {
                try
                {
                    var dbReader = app.Services.GetRequiredService<PreruleDatabaseReader>();
                    if (dbReader.IsAvailable)
                    {
                        var prerules = await dbReader.ReadAllAsync();
                        if (prerules.Count > 0)
                        {
                            store.LoadAll(prerules);
                            logger.LogInformation("数据库 fallback 加载前置规则: {Count} 条", prerules.Count);
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "数据库 fallback 也失败，前置规则为空");
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "从 Monitor Center 拉取全量监视项失败，将等待 Monitor Center 推送变更");
        }
    });
}

// ===== 配置同步 API =====
var syncGroup = app.MapGroup("/api/ruleengine/sync");

// Worker 拉取分配给自己的配置（通过 workerId 查询参数）
syncGroup.MapGet("/full/config", (string? workerId, ConfigurationService config) =>
{
    if (!string.IsNullOrEmpty(workerId))
    {
        var assigned = config.GetByWorkerId(workerId);
        return Results.Ok(assigned);
    }
    return Results.Ok(config.All.Values.ToList());
});

syncGroup.MapPost("/full", (SyncFullRequest request,
    ConfigurationService config,
    PartitionService partition,
    WorkerManager workers,
    ILogger<Program> logger) =>
{
    logger.LogInformation("全量同步: {Count} 个监视项, 版本 {Version}", request.Monitors.Count, request.Version);

    config.LoadFull(request.Monitors);

    var activeWorkers = workers.GetActiveWorkers();
    if (activeWorkers.Count > 0)
    {
        // 执行 Cost-Aware 贪心装箱分区，存储到 ConfigurationService
        var result = partition.Partition(request.Monitors, activeWorkers);
        config.SetWorkerAssignments(result.WorkerAssignments);

        // 更新每个 Worker 的 MonitorCount
        foreach (var (wid, monitors) in result.WorkerAssignments)
            workers.HeartbeatAsync(wid, monitors.Count);

        logger.LogInformation("分区完成: {WorkerCount} Worker, 平均 ~{AvgCount} 项/Worker",
            activeWorkers.Count, request.Monitors.Count / activeWorkers.Count);
    }
    else
    {
        logger.LogInformation("无活跃 Worker，配置已存储，等待 Worker 注册后分配");
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
    PartitionService partition,
    WorkerManager workers,
    ILogger<Program> logger) =>
{
    logger.LogInformation("增量同步: +{Added} ~{Modified} -{Deleted}, 版本 {Version}",
        request.Added.Count, request.Modified.Count, request.Deleted.Count, request.Version);

    if (request.Added.Count > 0) config.Add(request.Added);
    if (request.Modified.Count > 0) config.Update(request.Modified);
    if (request.Deleted.Count > 0) config.Remove(request.Deleted);

    // 增量变更后触发一次轻量重分区
    var activeWorkers = workers.GetActiveWorkers();
    if (activeWorkers.Count > 0)
    {
        var result = partition.Partition(config.All.Values.ToList(), activeWorkers);
        config.SetWorkerAssignments(result.WorkerAssignments);
    }

    return Results.Ok(new SyncResponse { Success = true, TotalMonitors = config.Count });
});

// ===== Monitor Center 联动 API =====
// Monitor Center 在监视项变更时调用此接口通知规则引擎重新拉取全量配置
var monitorGroup = app.MapGroup("/api/ruleengine/monitors");
monitorGroup.MapPost("/on-changed", async (SyncDeltaRequest request,
    ConfigurationService config,
    PartitionService partition,
    WorkerManager workers,
    HttpContext httpContext,
    ILogger<Program> logger) =>
{
    logger.LogInformation("MonitorCenter 推送变更通知: +{Added} ~{Modified} -{Deleted}, 版本 {Version}",
        request.Added.Count, request.Modified.Count, request.Deleted.Count, request.Version);

    var monitorCenterClient = httpContext.RequestServices.GetService<MonitorCenterClient>();
    if (monitorCenterClient != null)
    {
        try
        {
            // 重新从 Monitor Center 拉取全量监视项
            var monitors = await monitorCenterClient.FetchAllMonitorsAsync();
            config.LoadFull(monitors);

            var activeWorkers = workers.GetActiveWorkers();
            if (activeWorkers.Count > 0)
            {
                var result = partition.Partition(monitors, activeWorkers);
                config.SetWorkerAssignments(result.WorkerAssignments);
                logger.LogInformation("变更后重分区完成: {WorkerCount} Worker, {Count} 监视项",
                    activeWorkers.Count, monitors.Count);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "从 Monitor Center 重新拉取失败");
            return Results.Problem(detail: ex.Message, statusCode: 500);
        }
    }
    else
    {
        // 无 MonitorCenterClient 时，使用请求体中的数据做增量更新（向后兼容）
        if (request.Added.Count > 0) config.Add(request.Added);
        if (request.Modified.Count > 0) config.Update(request.Modified);
        if (request.Deleted.Count > 0) config.Remove(request.Deleted);

        var activeWorkers = workers.GetActiveWorkers();
        if (activeWorkers.Count > 0)
        {
            var result = partition.Partition(config.All.Values.ToList(), activeWorkers);
            config.SetWorkerAssignments(result.WorkerAssignments);
        }
    }

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
alarmGroup.MapPost("/history", async (AlarmQueryRequest request, HistoryAlarmService historySvc, ILogger<Program> logger) =>
{
    try
    {
        var result = await historySvc.QueryAsync(request);
        return Results.Ok(result);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "历史查询失败: {Message}", ex.Message);
        return Results.Problem(detail: ex.ToString(), statusCode: 500);
    }
});

alarmGroup.MapGet("/history/{monitorId}", async (string monitorId, HistoryAlarmService historySvc, ILogger<Program> logger) =>
{
    try
    {
        var result = await historySvc.QueryByMonitorAsync(monitorId);
        return Results.Ok(result);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "历史查询失败: {MonitorId}, {Message}", monitorId, ex.Message);
        return Results.Problem(detail: ex.ToString(), statusCode: 500);
    }
});

// 闭环验证: 检查是否有触发但无消除的报警事件
alarmGroup.MapGet("/history/closed-loop/validate", async (HistoryAlarmService historySvc, ILogger<Program> logger) =>
{
    try
    {
        var now = DateTime.UtcNow;
        var startTime = now.AddDays(-7);
        var result = await historySvc.ValidateClosedLoopAsync(startTime, now);
        return Results.Ok(result);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "闭环验证失败: {Message}", ex.Message);
        return Results.Problem(detail: ex.ToString(), statusCode: 500);
    }
});

// ===== Worker 注册 API =====
var workerGroup = app.MapGroup("/api/ruleengine/workers");

workerGroup.MapPost("/register", (WorkerInfo worker, WorkerManager workers,
    ConfigurationService config, PartitionService partition, ILogger<Program> logger) =>
{
    var id = workers.RegisterAsync(worker).Result;

    // 新 Worker 注册后，若有配置则触发一次分区分配
    if (config.Count > 0)
    {
        var activeWorkers = workers.GetActiveWorkers();
        var allMonitors = config.All.Values.ToList();
        var result = partition.Partition(allMonitors, activeWorkers);
        config.SetWorkerAssignments(result.WorkerAssignments);

        foreach (var (wid, monitors) in result.WorkerAssignments)
            workers.HeartbeatAsync(wid, monitors.Count);

        logger.LogInformation("Worker {WorkerId} 注册后触发重分区: {WorkerCount} Worker",
            worker.WorkerId, activeWorkers.Count);
    }

    return Results.Ok(new { workerId = id });
});

workerGroup.MapPost("/{workerId}/heartbeat", async (string workerId, int? monitorCount, WorkerManager workers) =>
{
    await workers.HeartbeatAsync(workerId, monitorCount ?? 0);
    return Results.Ok();
});

// ===== Admin API (运维) =====
var adminGroup = app.MapGroup("/api/ruleengine/admin");

adminGroup.MapPost("/load-prerules", async (HttpRequest request,
    PreruleDefinitionStore preruleStore,
    GrpcConnectionService grpcService,
    ConfigurationService config,
    PartitionService partition,
    WorkerManager workers,
    ILogger<Program> logger) =>
{
    try
    {
        using var reader = new StreamReader(request.Body);
        var json = await reader.ReadToEndAsync();
        var prerules = System.Text.Json.JsonSerializer.Deserialize<List<PreruleDefinition>>(json,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (prerules == null || prerules.Count == 0)
            return Results.BadRequest("空前置规则列表");

        preruleStore.LoadAll(prerules);
        logger.LogInformation("Admin: 加载 {Count} 条前置规则", prerules.Count);

        // 推送配置到所有Worker（包含前置规则）
        if (config.Count > 0)
        {
            var activeWorkers = workers.GetActiveWorkers();
            if (activeWorkers.Count > 0)
            {
                var allMonitors = config.All.Values.ToList();
                var result = partition.Partition(allMonitors, activeWorkers);
                config.SetWorkerAssignments(result.WorkerAssignments);
                await grpcService.PushToWorkersAsync(result.WorkerAssignments);
                logger.LogInformation("Admin: 已推送前置规则到 {WorkerCount} Workers", activeWorkers.Count);
            }
        }

        return Results.Ok(new { Count = prerules.Count });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "加载前置规则失败");
        return Results.Problem(detail: ex.ToString(), statusCode: 500);
    }
});

// ===== 健康检查 =====
app.MapGet("/api/ruleengine/health", (ConfigurationService config, WorkerManager workers) =>
{
    var dist = config.GetWorkerDistribution();
    return Results.Ok(new
    {
        Status = "healthy",
        MonitorCount = config.Count,
        ActiveWorkers = workers.ActiveCount,
        WorkerDistribution = dist,
        Timestamp = DateTime.UtcNow,
    });
});

// ===== 后台: gRPC 连接断开会自动注销 Worker 并重分区 =====
// 额外安全网: 每 30s 检查通过 HTTP 注册但无 gRPC 连接的 Worker，清理并重分区
_ = Task.Run(async () =>
{
    var logger = app.Services.GetRequiredService<ILoggerFactory>()
        .CreateLogger("GrpcWorkerSync");
    var grpcService = app.Services.GetRequiredService<GrpcConnectionService>();
    var workerManager = app.Services.GetRequiredService<WorkerManager>();
    var config = app.Services.GetRequiredService<ConfigurationService>();
    var partition = app.Services.GetRequiredService<PartitionService>();
    var timeout = TimeSpan.FromSeconds(60);

    while (true)
    {
        await Task.Delay(TimeSpan.FromSeconds(30));
        try
        {
            var connectedIds = new HashSet<string>(grpcService.GetConnectedWorkerIds());
            var deadWorkers = workerManager.GetDeadWorkers(timeout)
                .Where(w => !connectedIds.Contains(w.WorkerId))
                .ToList();

            if (deadWorkers.Count > 0)
            {
                foreach (var dead in deadWorkers)
                {
                    logger.LogWarning("Worker 无 gRPC 连接且心跳超时: {WorkerId}", dead.WorkerId);
                    await workerManager.DeregisterAsync(dead.WorkerId);
                }

                if (config.Count > 0)
                {
                    var activeWorkers = workerManager.GetActiveWorkers();
                    if (activeWorkers.Count > 0)
                    {
                        var allMonitors = config.All.Values.ToList();
                        var result = partition.Partition(allMonitors, activeWorkers);
                        config.SetWorkerAssignments(result.WorkerAssignments);
                        logger.LogInformation("安全网重分区: {ActiveWorkers} Worker, {MonitorCount} 监视项",
                            activeWorkers.Count, allMonitors.Count);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Worker 同步循环异常");
        }
    }
});

app.UseStaticFiles();

app.MapGrpcService<GrpcConnectionService>();

// ===== Dashboard =====
app.MapGet("/dashboard", () => Results.Redirect("/dashboard.html"));

app.MapGet("/api/ruleengine/dashboard/data", async (DashboardService dashboard) =>
{
    var data = await dashboard.GetDashboardDataAsync();
    return Results.Ok(data);
});

app.MapGet("/", () => "Luculent.Sis.RuleEngine Master (Control Plane)");

app.Run();
