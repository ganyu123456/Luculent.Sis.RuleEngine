using Luculent.Sis.RuleEngine.Shared.Interfaces;
using Luculent.Sis.RuleEngine.Shared.Models;
using Microsoft.Extensions.Logging;

namespace Luculent.Sis.RuleEngine.Worker.Storage;

/// <summary>
/// 生产环境复合报警写入器。
/// 实时操作 → Redis，历史操作 → ClickHouse。
/// </summary>
public class ProductionAlarmWriter : IAlarmWriter, IAsyncDisposable
{
    private readonly IAlarmWriter _realtime;   // Redis
    private readonly IAlarmWriter _history;    // ClickHouse
    private readonly ILogger<ProductionAlarmWriter> _logger;

    public ProductionAlarmWriter(
        IAlarmWriter realtimeWriter,
        IAlarmWriter historyWriter,
        ILogger<ProductionAlarmWriter> logger)
    {
        _realtime = realtimeWriter;
        _history = historyWriter;
        _logger = logger;
    }

    public async Task WriteRealtimeAlarmAsync(AlarmSnapshot alarm)
    {
        await _realtime.WriteRealtimeAlarmAsync(alarm);
    }

    public async Task ClearRealtimeAlarmAsync(string monitorId)
    {
        await _realtime.ClearRealtimeAlarmAsync(monitorId);
    }

    public async Task WriteHistoryAlarmAsync(AlarmEvent alarmEvent)
    {
        try
        {
            await _history.WriteHistoryAlarmAsync(alarmEvent);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "历史报警写入 ClickHouse 失败，不阻断实时链路");
        }
    }

    public Task<IReadOnlyList<AlarmSnapshot>> GetActiveAlarmsAsync()
    {
        return _realtime.GetActiveAlarmsAsync();
    }

    public Task<AlarmSnapshot?> GetAlarmAsync(string monitorId)
    {
        return _realtime.GetAlarmAsync(monitorId);
    }

    public async ValueTask DisposeAsync()
    {
        if (_history is IAsyncDisposable hd) await hd.DisposeAsync();
        if (_realtime is IAsyncDisposable rd) await rd.DisposeAsync();
        if (_realtime is IDisposable d) d.Dispose();
        _logger.LogInformation("ProductionAlarmWriter 已释放");
    }
}
