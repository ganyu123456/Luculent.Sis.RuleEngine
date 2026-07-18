using Luculent.Sis.RuleEngine.Shared.Models;
using Npgsql;

namespace Luculent.Sis.RuleEngine.Master.Services;

/// <summary>
/// 直接从 PostgreSQL 读取前置规则定义，作为 GetAllPrerules API 不可用时的 fallback。
/// </summary>
public class PreruleDatabaseReader
{
    private readonly string? _connectionString;
    private readonly ILogger<PreruleDatabaseReader> _logger;

    public PreruleDatabaseReader(IConfiguration configuration, ILogger<PreruleDatabaseReader> logger)
    {
        _connectionString = configuration.GetValue<string>("MonitorCenter:DatabaseConnection");
        _logger = logger;
    }

    public bool IsAvailable => !string.IsNullOrEmpty(_connectionString);

    public async Task<List<PreruleDefinition>> ReadAllAsync(CancellationToken ct = default)
    {
        if (!IsAvailable)
            return new List<PreruleDefinition>();

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        // ① 读取前置规则主表
        var prerules = new List<PreruleDefinition>();
        await using (var cmd = new NpgsqlCommand(
            "SELECT prerule_no, prerule_nam, prerule_dsc, refresh_cnt, source_no, enable_flag, rule_flag, lstusr_dtm FROM ssmcprerulemst WHERE valid_sta = 'A'", conn))
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                prerules.Add(new PreruleDefinition
                {
                    Id = reader.GetString(0),
                    Name = reader.GetString(1),
                    Desc = reader.IsDBNull(2) ? "" : reader.GetString(2),
                    RefreshIntervalSecond = reader.GetInt32(3),
                    FocusSourceId = reader.IsDBNull(4) ? "" : reader.GetString(4),
                    IsEnabled = reader.GetBoolean(5),
                    RuleType = reader.GetInt32(6),
                    LastModificationTime = reader.IsDBNull(7) ? DateTime.UtcNow : reader.GetDateTime(7),
                });
            }
        }

        if (prerules.Count == 0) return prerules;

        var preruleIds = prerules.Select(p => p.Id).ToList();

        // ② 读取数据源 (ssmcsourcemst group_no = prerule_no)
        var sourcesByPrerule = new Dictionary<string, List<PreruleSourceDefinition>>();
        await using (var cmd = new NpgsqlCommand(
            "SELECT group_no, source_id, source_flag, source_cod, unit_nam FROM ssmcsourcemst WHERE group_no = ANY($1) AND valid_sta = 'A'", conn))
        {
            cmd.Parameters.AddWithValue(preruleIds);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var groupNo = reader.GetString(0);
                if (!sourcesByPrerule.TryGetValue(groupNo, out var list))
                {
                    list = new List<PreruleSourceDefinition>();
                    sourcesByPrerule[groupNo] = list;
                }
                list.Add(new PreruleSourceDefinition
                {
                    Key = reader.GetString(1),
                    SourceType = reader.GetInt32(2),
                    SourceKey = reader.GetString(3),
                    Unit = reader.IsDBNull(4) ? "" : reader.GetString(4),
                });
            }
        }

        // ③ 读取 RangeDuration 规则 (ssmcrulerandurmst related_no = prerule_no)
        var rangeRulesByPrerule = new Dictionary<string, List<PreruleRangeDurationDefinition>>();
        await using (var cmd = new NpgsqlCommand(
            "SELECT related_no, ridur_no, left_id, symbol_flag, right_id, duration_cnt, enable_flag, status_no FROM ssmcrulerandurmst WHERE related_no = ANY($1) AND valid_sta = 'A'", conn))
        {
            cmd.Parameters.AddWithValue(preruleIds);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var relatedNo = reader.GetString(0);
                if (!rangeRulesByPrerule.TryGetValue(relatedNo, out var list))
                {
                    list = new List<PreruleRangeDurationDefinition>();
                    rangeRulesByPrerule[relatedNo] = list;
                }
                list.Add(new PreruleRangeDurationDefinition
                {
                    Id = reader.GetString(1),
                    LeftSourceKey = reader.IsDBNull(2) ? "" : reader.GetString(2),
                    SymbolType = reader.GetInt32(3),
                    RightSourceKey = reader.IsDBNull(4) ? "" : reader.GetString(4),
                    DurationSecond = reader.GetInt32(5),
                    IsEnabled = reader.GetBoolean(6),
                    MonitorStatusKey = reader.GetString(7),
                    Priority = 1,
                    BreakOnHit = false,
                });
            }
        }

        // ④ 读取 Expression 规则 (ssmcrulecodmst related_no = prerule_no)
        var expRulesByPrerule = new Dictionary<string, PreruleExpressionDefinition>();
        await using (var cmd = new NpgsqlCommand(
            "SELECT related_no, cod_no, cod_cod, status_no FROM ssmcrulecodmst WHERE related_no = ANY($1) AND valid_sta = 'A'", conn))
        {
            cmd.Parameters.AddWithValue(preruleIds);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var relatedNo = reader.GetString(0);
                expRulesByPrerule[relatedNo] = new PreruleExpressionDefinition
                {
                    Id = reader.GetString(1),
                    Code = reader.IsDBNull(2) ? "" : reader.GetString(2),
                    MonitorStatusId = reader.IsDBNull(3) ? "" : reader.GetString(3),
                };
            }
        }

        // ⑤ 拼装
        foreach (var p in prerules)
        {
            p.MonitorSources = sourcesByPrerule.GetValueOrDefault(p.Id) ?? new();
            p.RuleRangeDurations = rangeRulesByPrerule.GetValueOrDefault(p.Id) ?? new();
            p.RuleExpression = expRulesByPrerule.GetValueOrDefault(p.Id);
        }

        _logger.LogInformation("数据库直读前置规则: {Count} 条", prerules.Count);
        return prerules;
    }
}
