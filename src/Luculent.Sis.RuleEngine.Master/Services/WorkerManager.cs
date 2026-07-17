using System.Collections.Concurrent;

namespace Luculent.Sis.RuleEngine.Master.Services;

/// <summary>
/// Worker 生命周期管理器：注册、心跳、注销。
/// </summary>
public class WorkerManager
{
    private readonly ConcurrentDictionary<string, WorkerInfo> _workers = new();
    private readonly ILogger<WorkerManager> _logger;

    public WorkerManager(ILogger<WorkerManager> logger)
    {
        _logger = logger;
    }

    public Task<string> RegisterAsync(WorkerInfo worker)
    {
        worker.RegisteredAt = DateTime.UtcNow;
        worker.LastHeartbeat = DateTime.UtcNow;
        worker.Status = WorkerStatus.Online;
        _workers[worker.WorkerId] = worker;
        _logger.LogInformation("Worker 注册: {WorkerId} @ {Address}", worker.WorkerId, worker.GrpcAddress);
        return Task.FromResult(worker.WorkerId);
    }

    public Task HeartbeatAsync(string workerId)
    {
        if (_workers.TryGetValue(workerId, out var worker))
        {
            worker.LastHeartbeat = DateTime.UtcNow;
            if (worker.Status == WorkerStatus.Offline)
            {
                worker.Status = WorkerStatus.Online;
                _logger.LogInformation("Worker 恢复在线: {WorkerId}", workerId);
            }
        }
        return Task.CompletedTask;
    }

    public Task DeregisterAsync(string workerId)
    {
        _workers.TryRemove(workerId, out _);
        _logger.LogInformation("Worker 注销: {WorkerId}", workerId);
        return Task.CompletedTask;
    }

    public List<WorkerInfo> GetActiveWorkers()
    {
        return _workers.Values
            .Where(w => w.Status == WorkerStatus.Online)
            .ToList();
    }

    public List<WorkerInfo> GetDeadWorkers(TimeSpan timeout)
    {
        var cutoff = DateTime.UtcNow - timeout;
        return _workers.Values
            .Where(w => w.Status == WorkerStatus.Online && w.LastHeartbeat < cutoff)
            .ToList();
    }

    public WorkerInfo? GetWorker(string workerId)
    {
        _workers.TryGetValue(workerId, out var worker);
        return worker;
    }

    public int ActiveCount => _workers.Values.Count(w => w.Status == WorkerStatus.Online);
}
