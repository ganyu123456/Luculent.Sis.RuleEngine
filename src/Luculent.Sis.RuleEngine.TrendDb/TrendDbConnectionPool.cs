using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TrendDb_API;

namespace Luculent.Sis.RuleEngine.TrendDb;

/// <summary>
/// TrendDB 连接池：管理多个 Pool 实例，轮询分发读取请求。
/// 每个 Worker 进程持有独立连接池，多 Worker 部署时自然横向扩展。
/// </summary>
public sealed class TrendDbConnectionPool
{
    private readonly Pool[] _realTimePools;
    private int _realTimeIndex = -1;
    private readonly ILogger<TrendDbConnectionPool> _logger;

    public bool IsConnected { get; }

    public TrendDbConnectionPool(IOptions<TrendDbOptions> options, ILogger<TrendDbConnectionPool> logger)
    {
        _logger = logger;
        var connStr = options.Value.ConnectionString;

        // 跳过 "Type=TrendDB5;" 前缀
        var semiIdx = connStr.IndexOf(';');
        var cfgStr = semiIdx >= 0 ? connStr[(semiIdx + 1)..] : connStr;

        var poolSize = Math.Max(1, options.Value.RealTimePoolSize);
        _realTimePools = new Pool[poolSize];
        var allOk = true;

        for (int i = 0; i < poolSize; i++)
        {
            try
            {
                var pool = new Pool();
                var resList = new List<int>();
                var ret = pool.Add(cfgStr, ref resList);

                if (ret.Ok())
                {
                    _realTimePools[i] = pool;
                }
                else
                {
                    allOk = false;
                    _logger.LogWarning("TrendDB Pool #{Index} 连接失败: retCode={RetCode}, sysCode={SysCode}",
                        i, ret.retCode, ret.sysCode);
                }
            }
            catch (Exception ex)
            {
                allOk = false;
                _logger.LogError(ex, "TrendDB Pool #{Index} 初始化异常", i);
            }
        }

        IsConnected = allOk;
        _logger.LogInformation("TrendDB 连接池初始化完成: {Count} 个 Pool", poolSize);
    }

    public Pool NextRealTime()
    {
        var idx = Interlocked.Increment(ref _realTimeIndex);
        // 使用 & 而非 % 避免负数：取绝对值后取模
        var i = idx & int.MaxValue;
        return _realTimePools[i % _realTimePools.Length];
    }
}
