-- ===== SIS RuleEngine ClickHouse 初始化脚本 =====
-- 在容器首次启动时自动执行

CREATE DATABASE IF NOT EXISTS ruleengine;

-- ===== 报警事件表 =====
CREATE TABLE IF NOT EXISTS ruleengine.alarm_events (
    monitor_id        String COMMENT '监视项唯一 ID',
    monitor_key       String COMMENT '监视项唯一编码',
    monitor_name      String COMMENT '监视项名称',

    status_key        String COMMENT '状态键 (如 high_temp_alarm)',
    status_name       String COMMENT '状态名称 (如 超温报警)',
    event_type        Enum8('trigger' = 1, 'clear' = 2) COMMENT '事件类型: 触发/消除',

    occur_time        DateTime64(3) COMMENT '事件发生时间 (毫秒精度)',
    clear_time        Nullable(DateTime64(3)) COMMENT '报警消除时间',
    trigger_value     Float64 COMMENT '触发值',
    threshold_value   Nullable(Float64) COMMENT '阈值 (如有)',

    rule_type         UInt8 COMMENT '规则类型 (RuleType 枚举值)',
    config_version    DateTime64(3) COMMENT '规则配置版本号',
    worker_id         String COMMENT '产生此事件的 Worker ID',
    shard_id          UInt8 COMMENT '分片 ID',

    date              Date MATERIALIZED toDate(occur_time)
) ENGINE = MergeTree()
PARTITION BY toYYYYMMDD(occur_time)
ORDER BY (monitor_id, occur_time)
TTL occur_time + INTERVAL 365 DAY
SETTINGS index_granularity = 8192;

-- ===== 报警日统计物化视图 =====
CREATE MATERIALIZED VIEW IF NOT EXISTS ruleengine.alarm_daily_stats_mv
ENGINE = SummingMergeTree()
PARTITION BY toYYYYMM(date)
ORDER BY (date, monitor_id, status_key)
AS SELECT
    toDate(occur_time) AS date,
    monitor_id,
    monitor_key,
    monitor_name,
    status_key,
    status_name,
    count() AS alarm_count,
    max(trigger_value) AS max_trigger_value,
    min(trigger_value) AS min_trigger_value,
    avg(trigger_value) AS avg_trigger_value
FROM ruleengine.alarm_events
WHERE event_type = 'trigger'
GROUP BY date, monitor_id, monitor_key, monitor_name, status_key, status_name;
