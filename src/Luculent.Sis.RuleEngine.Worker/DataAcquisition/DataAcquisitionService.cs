using System.Diagnostics;
using Luculent.Sis.RuleEngine.Shared.Interfaces;
using Microsoft.Extensions.Logging;

namespace Luculent.Sis.RuleEngine.Worker.DataAcquisition;

/// <summary>
/// 数据采集后台服务：每秒从 TrendDB 拉取所有 tag 实时值，写入 TagValueStore 缓存。
/// 与计算循环解耦，避免每次计算都查询 TrendDB。
/// </summary>
public class DataAcquisitionService : BackgroundService
{
    private readonly ITrendDataReader _trendReader;
    private readonly TagValueStore _store;
    private readonly WorkerCalculationService _calcService;
    private readonly ILogger<DataAcquisitionService> _logger;

    public DataAcquisitionService(
        ITrendDataReader trendReader,
        TagValueStore store,
        WorkerCalculationService calcService,
        ILogger<DataAcquisitionService> logger)
    {
        _trendReader = trendReader;
        _store = store;
        _calcService = calcService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("数据采集服务启动 (1s 周期)");

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var tagNames = _calcService.GetAllTagNames();
            if (tagNames.Count == 0)
                continue;

            try
            {
                var sw = Stopwatch.StartNew();
                var values = await _trendReader.ReadBatchAsync(tagNames);
                _store.Update(values);
                sw.Stop();

                if (sw.ElapsedMilliseconds > 500)
                    _logger.LogWarning("TrendDB 采集偏慢: {ElapsedMs}ms, {TagCount} 个 tag",
                        sw.ElapsedMilliseconds, tagNames.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "TrendDB 数据采集失败: {TagCount} 个 tag", tagNames.Count);
            }
        }

        _logger.LogInformation("数据采集服务停止");
    }
}
