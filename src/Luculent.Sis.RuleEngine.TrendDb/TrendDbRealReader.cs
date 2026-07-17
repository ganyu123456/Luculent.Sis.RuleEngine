using Luculent.Sis.RuleEngine.Shared.Interfaces;
using Microsoft.Extensions.Logging;

namespace Luculent.Sis.RuleEngine.TrendDb;

/// <summary>
/// 真实 TrendDB 数据读取器，通过 TrendDb_API.dll 的 Pool.GetValueByTagName 读取实时值。
/// 使用 TrendDbConnectionPool 管理多 Pool 实例，支持横向扩展。
/// </summary>
public sealed class TrendDbRealReader : ITrendDataReader
{
    private readonly TrendDbConnectionPool _pool;
    private readonly ILogger<TrendDbRealReader> _logger;

    public bool IsConnected => _pool.IsConnected;

    public TrendDbRealReader(TrendDbConnectionPool pool, ILogger<TrendDbRealReader> logger)
    {
        _pool = pool;
        _logger = logger;
    }

    public Task<IDictionary<string, double?>> ReadBatchAsync(IEnumerable<string> tagNames)
    {
        var result = new Dictionary<string, double?>();
        var names = tagNames.ToList();
        if (names.Count == 0)
            return Task.FromResult<IDictionary<string, double?>>(result);

        var parsed = ParseTagNames(names);

        foreach (var group in parsed.GroupBy(p => p.dbName))
        {
            var dbName = group.Key;
            var shortNames = group.Select(g => g.shortName).ToList();
            var fullNames = group.Select(g => g.fullName).ToList();

            var tagValueList = new List<Ld.COMMON.TagValue>();
            var resList = new List<int>();

            try
            {
                var pool = _pool.NextRealTime();
                var ret = pool.GetValueByTagName(dbName, shortNames, ref tagValueList, ref resList);
                if (!ret.Ok())
                {
                    _logger.LogWarning("TrendDB 实时读取失败: db={DbName}, retCode={RetCode}", dbName, ret.retCode);
                    foreach (var fn in fullNames)
                        result[fn] = null;
                    continue;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "TrendDB 实时读取异常: db={DbName}", dbName);
                foreach (var fn in fullNames)
                    result[fn] = null;
                continue;
            }

            for (int i = 0; i < shortNames.Count; i++)
                result[fullNames[i]] = i < tagValueList.Count ? ConvertToDouble(tagValueList[i]) : null;
        }

        return Task.FromResult<IDictionary<string, double?>>(result);
    }

    public Task<IDictionary<string, double?>> ReadHistoryBatchAsync(
        IEnumerable<string> tagNames, DateTime timestamp)
    {
        var result = new Dictionary<string, double?>();
        var names = tagNames.ToList();
        if (names.Count == 0)
            return Task.FromResult<IDictionary<string, double?>>(result);

        var parsed = ParseTagNames(names);
        var ts = ConvertToTimestamp(timestamp);

        foreach (var group in parsed.GroupBy(p => p.dbName))
        {
            var dbName = group.Key;
            var shortNames = group.Select(g => g.shortName).ToList();
            var fullNames = group.Select(g => g.fullName).ToList();

            var tagValueList = new List<Ld.COMMON.TagValue>();
            var resList = new List<int>();

            try
            {
                var pool = _pool.NextRealTime();
                var ret = pool.GetHisTimePointValueByNames(dbName, shortNames, ts,
                    Ld.COMMON.ResampleMode.kResampleSuggest, ref tagValueList, ref resList);
                if (!ret.Ok())
                {
                    _logger.LogWarning("TrendDB 历史读取失败: db={DbName}, retCode={RetCode}", dbName, ret.retCode);
                    foreach (var fn in fullNames)
                        result[fn] = null;
                    continue;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "TrendDB 历史读取异常: db={DbName}", dbName);
                foreach (var fn in fullNames)
                    result[fn] = null;
                continue;
            }

            for (int i = 0; i < shortNames.Count; i++)
                result[fullNames[i]] = i < tagValueList.Count ? ConvertToDouble(tagValueList[i]) : null;
        }

        return Task.FromResult<IDictionary<string, double?>>(result);
    }

    private static List<(string fullName, string dbName, string shortName)> ParseTagNames(List<string> names)
    {
        var parsed = new List<(string fullName, string dbName, string shortName)>();
        foreach (var tag in names)
        {
            if (string.IsNullOrEmpty(tag)) continue;
            if (tag.Contains('.'))
            {
                var parts = tag.Split('.');
                parsed.Add((tag, parts[0], string.Join(".", parts.Skip(1))));
            }
            else
            {
                parsed.Add((tag, "", tag));
            }
        }
        return parsed;
    }

    private static ulong ConvertToTimestamp(DateTime dt)
    {
        var dto = new DateTimeOffset(dt.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(dt, DateTimeKind.Utc)
            : dt);
        return Convert.ToUInt64(dto.ToUnixTimeMilliseconds());
    }

    private static double? ConvertToDouble(Ld.COMMON.TagValue tv)
    {
        if (tv.GetValueStatus() != 1)
            return null;

        return tv.GetValueType() switch
        {
            Ld.COMMON.ValueType.kLong => Convert.ToDouble(tv.GetLongValue()),
            Ld.COMMON.ValueType.kULong => Convert.ToDouble(tv.GetULongValue()),
            Ld.COMMON.ValueType.kFloat => Convert.ToDouble(tv.GetFloatValue()),
            Ld.COMMON.ValueType.kInt => Convert.ToDouble(tv.GetIntValue()),
            Ld.COMMON.ValueType.kShort => Convert.ToDouble(tv.GetShortValue()),
            Ld.COMMON.ValueType.kDouble => tv.GetDoubleValue(),
            Ld.COMMON.ValueType.kBool => tv.GetBoolValue() ? 1.0 : 0.0,
            _ => null,
        };
    }
}
