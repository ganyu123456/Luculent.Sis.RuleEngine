-- ===== SIS RuleEngine ClickHouse 初始化脚本 =====
-- 事件流模型 v2: 无 trigger/clear 区分，纯状态变更追加写
-- 区间配对由 LEAD 窗口函数在查询侧完成

CREATE DATABASE IF NOT EXISTS ruleengine;

-- ===== 报警事件表 (事件流模型) =====
CREATE TABLE IF NOT EXISTS ruleengine.alarm_events (
    monitor_id        String COMMENT '监视项唯一 ID',
    monitor_key       String COMMENT '监视项唯一编码',
    monitor_name      String COMMENT '监视项名称',

    status_key        String COMMENT '状态键 (空字符串表示恢复正常)',
    status_name       Nullable(String) COMMENT '状态名称',

    occur_time        DateTime64(3, 'UTC') COMMENT '事件发生时间 (毫秒精度)',
    trigger_value     Float64 COMMENT '触发值',
    threshold_value   Nullable(Float64) COMMENT '阈值 (如有)',

    rule_type         UInt8 COMMENT '规则类型 (RuleType 枚举值)',
    config_version    DateTime64(3, 'UTC') COMMENT '规则配置版本号',
    worker_id         String COMMENT '产生此事件的 Worker ID',
    shard_id          UInt8 COMMENT '分片 ID',

    last_event_id     Nullable(String) COMMENT '上一次事件 ID',
    last_event_name   Nullable(String) COMMENT '上一次事件状态名称',
    unit              Nullable(String) COMMENT '单位',
    job_id            Nullable(String) COMMENT '任务 ID',

    date              Date MATERIALIZED toDate(occur_time)
) ENGINE = MergeTree()
PARTITION BY toYYYYMMDD(date)
ORDER BY (monitor_id, status_key, occur_time)
TTL date + INTERVAL 90 DAY
SETTINGS index_granularity = 8192;

-- ===== 历史迁移兼容列 (从 v1 trigger/clear 模型升级时保留) =====
-- 以下列在 v1 表中存在但新模型不再使用，通过 ALTER TABLE 保留以兼容旧数据:
--   event_type      Enum8('trigger' = 1, 'clear' = 2)  -- 已废弃，查询侧不再区分
--   clear_time      Nullable(DateTime64(3))              -- 已废弃，由 LEAD 窗口函数替代
