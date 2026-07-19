-- ============================================================================
-- SIS RuleEngine: 100,000 监视项全规则类型性能压测数据 (PostgreSQL)
-- ============================================================================
-- 目标: 暴露 100 万级规模下的性能瓶颈
--   P0-1: CalculateRuleExpression 对全量 tag 做正则替换 → O(N²)
--   P0-2: Parallel.ForEachAsync 16 并发 vs 10K+ 到期/周期
--   P1:   GetAllTagNames 每秒遍历 10 万监视项
--   P1:   独立 SaveAsync × 10 万 → state store IO
--   P2:   Redis SMEMBERS 5 万条活跃报警
-- ============================================================================
--
-- 规则类型分布 (100,000 监视项):
--   Expression              30,000 (30%) ← 主要压测目标
--   RangeDuration           15,000 (15%)
--   RangeFrequency          10,000 (10%)
--   FeatureValue            10,000 (10%)
--   PackageValue            10,000 (10%)
--   WallTemperature          5,000 ( 5%)
--   InterfaceMonitoring      5,000 ( 5%)
--   RulePackageValue        10,000 (10%)
--   MultiStateRangeDuration  5,000 ( 5%)
--
-- 每个监视项使用独立 tag (perf.tag.{N}) → TagValueStore 有 100K 条目
-- 阈值大量差异化 → 避免所有监视项同时触发/消除
-- 刷新间隔混合 1/5/10/30/60s → 模拟真实负载波动
--
-- 使用方式:
--   1. 在 MonitorCenter PostgreSQL 中执行此 SQL
--   2. MonitorCenter API 将自动服务这些数据
--   3. 启动 RuleEngine (docker compose up -d --scale worker=5)
--   4. POST /api/ruleengine/sync/full 触发全量同步
--   5. 观察日志: 周期延迟、GC 暂停、Redis/ClickHouse 吞吐
-- ============================================================================

-- ===== 清理旧压测数据 =====
DELETE FROM ssmcrulerandurmst WHERE related_no LIKE 'PERF%';
DELETE FROM ssmcrulecodmst    WHERE related_no LIKE 'PERF%';
DELETE FROM ssmcsourcemst     WHERE group_no   LIKE 'PERF%';
DELETE FROM ssmcitemmst       WHERE monitor_no LIKE 'PERF%';
DELETE FROM ssmcprerulemst    WHERE prerule_no LIKE 'PERF-PRERULE%';

-- ===== 0. 确保 MonitorStatus 参考数据存在 =====
-- 如果 ssmcstatusmst 中没有这些 status_no，请先创建
-- 这里使用与现有测试数据一致的 status_no

-- ===== 1. 创建 5 个前置规则 =====

-- 前置规则 A: Expression — tag_value > 20
INSERT INTO ssmcprerulemst (prerule_no, prerule_nam, prerule_dsc, refresh_cnt, source_no, enable_flag, rule_flag, fstusr_dtm, lstusr_dtm, valid_sta, org_no)
VALUES ('PERF-PRERULE-A', 'PerfPreruleA-Expr',  'perf.prerule.val > 20',           60, '', true, 1, NOW(), NOW(), 'A', -1);

-- 前置规则 B: Expression — tag_value > 10
INSERT INTO ssmcprerulemst (prerule_no, prerule_nam, prerule_dsc, refresh_cnt, source_no, enable_flag, rule_flag, fstusr_dtm, lstusr_dtm, valid_sta, org_no)
VALUES ('PERF-PRERULE-B', 'PerfPreruleB-Expr',  'perf.prerule.val > 10',           60, '', true, 1, NOW(), NOW(), 'A', -1);

-- 前置规则 C: RangeDuration — val >= 30 持续 5s
INSERT INTO ssmcprerulemst (prerule_no, prerule_nam, prerule_dsc, refresh_cnt, source_no, enable_flag, rule_flag, fstusr_dtm, lstusr_dtm, valid_sta, org_no)
VALUES ('PERF-PRERULE-C', 'PerfPreruleC-RDur',  'val >= 30 for 5s',               60, '', true, 2, NOW(), NOW(), 'A', -1);

-- 前置规则 D: RangeDuration — val < 80 持续 10s
INSERT INTO ssmcprerulemst (prerule_no, prerule_nam, prerule_dsc, refresh_cnt, source_no, enable_flag, rule_flag, fstusr_dtm, lstusr_dtm, valid_sta, org_no)
VALUES ('PERF-PRERULE-D', 'PerfPreruleD-RDur',  'val < 80 for 10s',              60, '', true, 2, NOW(), NOW(), 'A', -1);

-- 前置规则 E: Expression — 复杂多条件 (压测表达式解析)
INSERT INTO ssmcprerulemst (prerule_no, prerule_nam, prerule_dsc, refresh_cnt, source_no, enable_flag, rule_flag, fstusr_dtm, lstusr_dtm, valid_sta, org_no)
VALUES ('PERF-PRERULE-E', 'PerfPreruleE-Expr',  'val > 10 && val < 95',          30, '', true, 1, NOW(), NOW(), 'A', -1);

-- 前置规则数据源 (共用 RealDB tag)
INSERT INTO ssmcsourcemst (source_no, group_no, source_id, unit_nam, source_flag, source_cod, fstusr_dtm, lstusr_dtm, valid_sta, org_no)
VALUES
('PERF-PRSRC-REAL', 'PERF-PRERULE-A', 'perf.prerule.val', '', 3, 'db05.test1', NOW(), NOW(), 'A', -1),
('PERF-PRSRC-REAL', 'PERF-PRERULE-B', 'perf.prerule.val', '', 3, 'db05.test1', NOW(), NOW(), 'A', -1),
('PERF-PRSRC-REAL', 'PERF-PRERULE-C', 'perf.prerule.val', '', 3, 'db05.test1', NOW(), NOW(), 'A', -1),
('PERF-PRSRC-REAL', 'PERF-PRERULE-D', 'perf.prerule.val', '', 3, 'db05.test1', NOW(), NOW(), 'A', -1),
('PERF-PRSRC-REAL', 'PERF-PRERULE-E', 'val',              '', 3, 'db05.test1', NOW(), NOW(), 'A', -1);

-- 前置规则 A/B/E 表达式
INSERT INTO ssmcrulecodmst (cod_no, cod_cod, status_no, related_no, fstusr_dtm, lstusr_dtm, valid_sta, org_no)
VALUES
('PERF-PREXP-A', 'perf.prerule.val > 20',          '39edc1419d86d38933a57f1e156b4991', 'PERF-PRERULE-A', NOW(), NOW(), 'A', -1),
('PERF-PREXP-B', 'perf.prerule.val > 10',          '39edc1419d86d38933a57f1e156b4991', 'PERF-PRERULE-B', NOW(), NOW(), 'A', -1),
('PERF-PREXP-E', 'val > 10 && val < 95',           '39edc1419d86d38933a57f1e156b4991', 'PERF-PRERULE-E', NOW(), NOW(), 'A', -1);

-- 前置规则 C: val >= 30, duration=5s
INSERT INTO ssmcrulerandurmst (ridur_no, statuslin_cod, status_no, related_no, enable_flag, left_id, symbol_flag, right_id, duration_cnt, fstusr_dtm, lstusr_dtm, valid_sta, org_no)
VALUES ('PERF-PRRULE-C', 'ok', '88a2ff2f827f487e83bda61736ca2b2d', 'PERF-PRERULE-C', true, 'perf.prerule.val', 2, '30', 5, NOW(), NOW(), 'A', -1);

-- 前置规则 D: val < 80, duration=10s
INSERT INTO ssmcrulerandurmst (ridur_no, statuslin_cod, status_no, related_no, enable_flag, left_id, symbol_flag, right_id, duration_cnt, fstusr_dtm, lstusr_dtm, valid_sta, org_no)
VALUES ('PERF-PRRULE-D', 'ok', '88a2ff2f827f487e83bda61736ca2b2d', 'PERF-PRERULE-D', true, 'perf.prerule.val', 4, '80', 10, NOW(), NOW(), 'A', -1);


-- ============================================================================
-- 2. 批量创建 100,000 个监视项 (按规则类型分 9 个批次)
-- ============================================================================
-- 每个监视项使用唯一 tag 名: perf.tag.{N}
-- 这样 TagValueStore 中会有 100K 条目，触发 Expression 规则的 O(N²) 瓶颈
-- ============================================================================

-- 状态键定义(供各规则类型使用，与 MonitorCenter 保持一致):
--   normal / alarm_high / alarm_low / warning / critical / fault
--   壁温: wall_temp_high / wall_temp_critical
--   接口监控: CollectError / InterfaceError / ManualStop / ShutDown
--   打包点: PACKAGE_BIT_{N} / PACKAGECOMPLETEEVENT

-- 前置规则关联策略:
--   监视项序号 % 5 == 0 → PERF-PRERULE-A
--   监视项序号 % 5 == 1 → PERF-PRERULE-B
--   监视项序号 % 5 == 2 → PERF-PRERULE-C
--   监视项序号 % 5 == 3 → PERF-PRERULE-D
--   监视项序号 % 5 == 4 → PERF-PRERULE-E
--   前 5000 个 InterfaceMonitoring → 无前置规则 (用 ManualFlag/StopMonitor 抑制)


-- ========================================================================
-- 2a. Expression 规则 × 30,000 (rule_flag = 1)
--     表达式: tag_value > threshold  (简单比较)
--     threshold 范围: 10 ~ 90 (多样化，错峰触发)
--     refresh: 混合 1/5/10/30/60s
--     这是最大的瓶颈来源 —— 每个表达式的 data 参数包含全部 100K tag
-- ========================================================================

DO $$
DECLARE
    base_idx  INTEGER := 0;
    total     INTEGER := 30000;
    i         INTEGER;
    seq       INTEGER;
    mon_no    VARCHAR(32);
    mon_id    VARCHAR(32);
    mon_nam   VARCHAR(64);
    tag_name  VARCHAR(64);
    thr_src_id VARCHAR(64);
    thr_val   INTEGER;
    refresh   INTEGER;
    prerule   VARCHAR(32);
    src1_no   VARCHAR(32);
    src2_no   VARCHAR(32);
    cod_no    VARCHAR(32);
    expr_code VARCHAR(256);
    status_no VARCHAR(64);
BEGIN
    status_no := '39edc1419d86d38933a57f1e156b4991';

    FOR i IN 0 .. (total - 1) LOOP
        seq      := base_idx + i;
        mon_no   := 'PERF' || LPAD(seq::TEXT, 7, '0');
        mon_id   := 'perf-expr-' || LPAD(i::TEXT, 5, '0');
        mon_nam  := 'PerfExpr ' || LPAD(i::TEXT, 5, '0');
        tag_name := 'perf.tag.' || LPAD(seq::TEXT, 6, '0');
        thr_val  := 10 + (i % 81);          -- 10~90
        refresh  := CASE (i % 5)
                      WHEN 0 THEN 1         --  6,000 个 1s 刷新 (高频)
                      WHEN 1 THEN 5         --  6,000 个 5s 刷新
                      WHEN 2 THEN 10        --  6,000 个 10s 刷新
                      WHEN 3 THEN 30        --  6,000 个 30s 刷新
                      ELSE 60               --  6,000 个 60s 刷新
                    END;
        prerule  := CASE (i % 5)
                      WHEN 0 THEN 'PERF-PRERULE-A'
                      WHEN 1 THEN 'PERF-PRERULE-B'
                      WHEN 2 THEN 'PERF-PRERULE-C'
                      WHEN 3 THEN 'PERF-PRERULE-D'
                      ELSE 'PERF-PRERULE-E'
                    END;

        thr_src_id := 'thr_' || mon_id;

        -- MonitorItem
        INSERT INTO ssmcitemmst (monitor_no, monitor_id, monitor_nam, monitor_dsc, status_no, refresh_cnt, source_no, enable_flag, prerule_no, rule_flag, node_no, manual_flag, stop_monitor_key, fail_limit, fstusr_dtm, lstusr_dtm, valid_sta, org_no)
        VALUES (mon_no, mon_id, mon_nam, '', status_no, refresh, tag_name, true, prerule, 1,
                '88a2ff2f827f487e83bda6173afa1234', 1, '', NULL, NOW(), NOW(), 'A', -1);

        -- Source 1: RealDB tag → TrendDB 唯一 tag 名
        src1_no := 'PERF' || LPAD((seq * 3)::TEXT, 7, '0');
        INSERT INTO ssmcsourcemst (source_no, group_no, source_id, unit_nam, source_flag, source_cod, fstusr_dtm, lstusr_dtm, valid_sta, org_no)
        VALUES (src1_no, mon_no, tag_name, '%', 3, tag_name, NOW(), NOW(), 'A', -1);

        -- Source 2: Static 阈值 (source_flag=1, value 作为常量)
        src2_no := 'PERF' || LPAD((seq * 3 + 1)::TEXT, 7, '0');
        INSERT INTO ssmcsourcemst (source_no, group_no, source_id, unit_nam, source_flag, source_cod, fstusr_dtm, lstusr_dtm, valid_sta, org_no)
        VALUES (src2_no, mon_no, thr_src_id, '%', 1, thr_val::TEXT, NOW(), NOW(), 'A', -1);

        -- 表达式规则: tag_value > threshold
        cod_no    := 'PERF' || LPAD((seq * 3 + 2)::TEXT, 7, '0');
        expr_code := tag_name || ' > ' || thr_src_id;
        INSERT INTO ssmcrulecodmst (cod_no, cod_cod, status_no, related_no, fstusr_dtm, lstusr_dtm, valid_sta, org_no)
        VALUES (cod_no, expr_code, status_no, mon_no, NOW(), NOW(), 'A', -1);
    END LOOP;
END $$;


-- ========================================================================
-- 2b. RangeDuration 区间时长规则 × 15,000 (rule_flag = 2)
--     left: perf.tag.{N} (RealDB)  right: 阈值 (Static)
--     symbol: 1=Greater 2=GreaterOrEqual 3=Less 4=LessOrEqual
--     duration: 0~60s (0=立即触发)
-- ========================================================================

DO $$
DECLARE
    base_idx  INTEGER := 30000;
    total     INTEGER := 15000;
    i         INTEGER;
    seq       INTEGER;
    mon_no    VARCHAR(32);
    mon_id    VARCHAR(32);
    mon_nam   VARCHAR(64);
    tag_name  VARCHAR(64);
    thr_val   INTEGER;
    refresh   INTEGER;
    prerule   VARCHAR(32);
    src1_no   VARCHAR(32);
    src2_no   VARCHAR(32);
    rule_no   VARCHAR(32);
    symbol    INTEGER;
    duration  INTEGER;
    status_cod VARCHAR(32);
    status_no VARCHAR(64);
BEGIN
    status_no := '39edc1419d86d38933a57f1e156b4991';

    FOR i IN 0 .. (total - 1) LOOP
        seq       := base_idx + i;
        mon_no    := 'PERF' || LPAD(seq::TEXT, 7, '0');
        mon_id    := 'perf-rdur-' || LPAD(i::TEXT, 5, '0');
        mon_nam   := 'PerfRDur ' || LPAD(i::TEXT, 5, '0');
        tag_name  := 'perf.tag.' || LPAD(seq::TEXT, 6, '0');
        thr_val   := 20 + (i % 71);         -- 20~90
        refresh   := CASE (i % 4)
                       WHEN 0 THEN 5
                       WHEN 1 THEN 10
                       WHEN 2 THEN 30
                       ELSE 60
                     END;
        prerule   := CASE (i % 5)
                       WHEN 0 THEN 'PERF-PRERULE-A'
                       WHEN 1 THEN 'PERF-PRERULE-B'
                       WHEN 2 THEN 'PERF-PRERULE-C'
                       WHEN 3 THEN 'PERF-PRERULE-D'
                       ELSE 'PERF-PRERULE-E'
                     END;
        symbol    := 1 + (i % 4);            -- 1~4: > >= < <=
        duration  := (i % 61);               -- 0~60s (0=立即触发)
        status_cod := CASE (i % 4)
                        WHEN 0 THEN 'alarm_high'
                        WHEN 1 THEN 'warning'
                        WHEN 2 THEN 'alarm_low'
                        ELSE 'critical'
                      END;

        INSERT INTO ssmcitemmst (monitor_no, monitor_id, monitor_nam, monitor_dsc, status_no, refresh_cnt, source_no, enable_flag, prerule_no, rule_flag, node_no, manual_flag, stop_monitor_key, fail_limit, fstusr_dtm, lstusr_dtm, valid_sta, org_no)
        VALUES (mon_no, mon_id, mon_nam, '', status_no, refresh, tag_name, true, prerule, 2,
                '88a2ff2f827f487e83bda6173afa1234', 1, '', NULL, NOW(), NOW(), 'A', -1);

        -- RealDB tag
        src1_no := 'PERF' || LPAD((seq * 3)::TEXT, 7, '0');
        INSERT INTO ssmcsourcemst (source_no, group_no, source_id, unit_nam, source_flag, source_cod, fstusr_dtm, lstusr_dtm, valid_sta, org_no)
        VALUES (src1_no, mon_no, tag_name, '%', 3, tag_name, NOW(), NOW(), 'A', -1);

        -- Static 阈值 (right side)
        src2_no := 'PERF' || LPAD((seq * 3 + 1)::TEXT, 7, '0');
        INSERT INTO ssmcsourcemst (source_no, group_no, source_id, unit_nam, source_flag, source_cod, fstusr_dtm, lstusr_dtm, valid_sta, org_no)
        VALUES (src2_no, mon_no, 'thr_' || mon_id, '%', 1, thr_val::TEXT, NOW(), NOW(), 'A', -1);

        -- RangeDuration 规则
        rule_no := 'PERF' || LPAD((seq * 3 + 2)::TEXT, 7, '0');
        INSERT INTO ssmcrulerandurmst (ridur_no, statuslin_cod, status_no, related_no, enable_flag, left_id, symbol_flag, right_id, duration_cnt, fstusr_dtm, lstusr_dtm, valid_sta, org_no)
        VALUES (rule_no, status_cod, status_no, mon_no, true, tag_name, symbol, 'thr_' || mon_id, duration, NOW(), NOW(), 'A', -1);
    END LOOP;
END $$;


-- ========================================================================
-- 2c. RangeFrequency 区间频率规则 × 10,000 (rule_flag = 3)
--     频率: N 次/窗口秒数, left=tag right=阈值, symbol 多样化
--     使用 ssmcrulerandurmst 表: statuslin_cod 前缀区分 + 自定义字段
--     NOTE: 若 MonitorCenter 使用独立表 ssmcrulefrqumst，请调整
-- ========================================================================

DO $$
DECLARE
    base_idx  INTEGER := 45000;
    total     INTEGER := 10000;
    i         INTEGER;
    seq       INTEGER;
    mon_no    VARCHAR(32);
    mon_id    VARCHAR(32);
    mon_nam   VARCHAR(64);
    tag_name  VARCHAR(64);
    thr_val   INTEGER;
    refresh   INTEGER;
    prerule   VARCHAR(32);
    freq_cnt  INTEGER;
    window_sec INTEGER;
    symbol    INTEGER;
    src1_no   VARCHAR(32);
    src2_no   VARCHAR(32);
    rule_no   VARCHAR(32);
    status_no VARCHAR(64);
BEGIN
    status_no := '39edc1419d86d38933a57f1e156b4991';

    FOR i IN 0 .. (total - 1) LOOP
        seq        := base_idx + i;
        mon_no     := 'PERF' || LPAD(seq::TEXT, 7, '0');
        mon_id     := 'perf-rfreq-' || LPAD(i::TEXT, 5, '0');
        mon_nam    := 'PerfRFreq ' || LPAD(i::TEXT, 5, '0');
        tag_name   := 'perf.tag.' || LPAD(seq::TEXT, 6, '0');
        thr_val    := 30 + (i % 51);          -- 30~80
        refresh    := CASE (i % 3)
                        WHEN 0 THEN 10
                        WHEN 1 THEN 30
                        ELSE 60
                      END;
        prerule    := CASE (i % 5)
                        WHEN 0 THEN 'PERF-PRERULE-A'
                        WHEN 1 THEN 'PERF-PRERULE-B'
                        WHEN 2 THEN 'PERF-PRERULE-C'
                        WHEN 3 THEN 'PERF-PRERULE-D'
                        ELSE 'PERF-PRERULE-E'
                      END;
        freq_cnt   := 2 + (i % 10);           -- 2~11 次
        window_sec := 30 + (i % 5) * 30;      -- 30/60/90/120/150s
        symbol     := 1 + (i % 4);            -- > >= < <=

        INSERT INTO ssmcitemmst (monitor_no, monitor_id, monitor_nam, monitor_dsc, status_no, refresh_cnt, source_no, enable_flag, prerule_no, rule_flag, node_no, manual_flag, stop_monitor_key, fail_limit, fstusr_dtm, lstusr_dtm, valid_sta, org_no)
        VALUES (mon_no, mon_id, mon_nam, '', status_no, refresh, tag_name, true, prerule, 3,
                '88a2ff2f827f487e83bda6173afa1234', 1, '', NULL, NOW(), NOW(), 'A', -1);

        src1_no := 'PERF' || LPAD((seq * 3)::TEXT, 7, '0');
        INSERT INTO ssmcsourcemst (source_no, group_no, source_id, unit_nam, source_flag, source_cod, fstusr_dtm, lstusr_dtm, valid_sta, org_no)
        VALUES (src1_no, mon_no, tag_name, '%', 3, tag_name, NOW(), NOW(), 'A', -1);

        src2_no := 'PERF' || LPAD((seq * 3 + 1)::TEXT, 7, '0');
        INSERT INTO ssmcsourcemst (source_no, group_no, source_id, unit_nam, source_flag, source_cod, fstusr_dtm, lstusr_dtm, valid_sta, org_no)
        VALUES (src2_no, mon_no, 'thr_' || mon_id, '%', 1, thr_val::TEXT, NOW(), NOW(), 'A', -1);

        -- RangeFrequency 规则 (复用 ssmcrulerandurmst, statuslin_cod 带 freq 前缀区分)
        -- MonitorCenter 在 GetAllMonitors API 中根据 rule_flag=3 解析为 RangeFrequencyRules
        rule_no := 'PERF' || LPAD((seq * 3 + 2)::TEXT, 7, '0');
        INSERT INTO ssmcrulerandurmst (ridur_no, statuslin_cod, status_no, related_no, enable_flag, left_id, symbol_flag, right_id, duration_cnt, fstusr_dtm, lstusr_dtm, valid_sta, org_no)
        VALUES (rule_no, 'freq_alarm', status_no, mon_no, true, tag_name, symbol, 'thr_' || mon_id, freq_cnt * 1000 + window_sec, NOW(), NOW(), 'A', -1);
        -- NOTE: duration_cnt 字段编码: freq_cnt * 1000 + window_sec → MonitorCenter 解析
        --       若 MonitorCenter 使用独立 ssmcrulefrqumst 表，需调整 INSERT 目标表
    END LOOP;
END $$;


-- ========================================================================
-- 2d. FeatureValue 特征值规则 × 10,000 (rule_flag = 4)
--     每个监视项有 MonitorStatusDefinitions 含 TriggerValueDefDic
--     focus_id → 获取值 → 在 TriggerValueDefDic 中匹配状态键
--     NOTE: TriggerValueDefDic 在 MonitorCenter 中来自 ssmcstatusdtlmst
--           此处创建 ssmcitemmst + ssmcsourcemst, status 定义由 MonitorCenter 管理
-- ========================================================================

DO $$
DECLARE
    base_idx  INTEGER := 55000;
    total     INTEGER := 10000;
    i         INTEGER;
    seq       INTEGER;
    mon_no    VARCHAR(32);
    mon_id    VARCHAR(32);
    mon_nam   VARCHAR(64);
    tag_name  VARCHAR(64);
    focus_id  VARCHAR(64);
    refresh   INTEGER;
    prerule   VARCHAR(32);
    src1_no   VARCHAR(32);
    status_no VARCHAR(64);
BEGIN
    status_no := '88a2ff2f827f487e83bda61736ca3ad1';

    FOR i IN 0 .. (total - 1) LOOP
        seq      := base_idx + i;
        mon_no   := 'PERF' || LPAD(seq::TEXT, 7, '0');
        mon_id   := 'perf-feat-' || LPAD(i::TEXT, 5, '0');
        mon_nam  := 'PerfFeat ' || LPAD(i::TEXT, 5, '0');
        tag_name := 'perf.tag.' || LPAD(seq::TEXT, 6, '0');
        focus_id := 'perf.feat.focus.' || LPAD(seq::TEXT, 6, '0');
        refresh  := CASE (i % 4)
                      WHEN 0 THEN 10
                      WHEN 1 THEN 30
                      WHEN 2 THEN 60
                      ELSE 120
                    END;
        prerule  := CASE (i % 5)
                      WHEN 0 THEN 'PERF-PRERULE-A'
                      WHEN 1 THEN 'PERF-PRERULE-B'
                      WHEN 2 THEN 'PERF-PRERULE-C'
                      WHEN 3 THEN 'PERF-PRERULE-D'
                      ELSE 'PERF-PRERULE-E'
                    END;

        -- source_no = focus_id → 映射到 FocusSourceId
        INSERT INTO ssmcitemmst (monitor_no, monitor_id, monitor_nam, monitor_dsc, status_no, refresh_cnt, source_no, enable_flag, prerule_no, rule_flag, node_no, manual_flag, stop_monitor_key, fail_limit, fstusr_dtm, lstusr_dtm, valid_sta, org_no)
        VALUES (mon_no, mon_id, mon_nam, '', status_no, refresh, focus_id, true, prerule, 4,
                '88a2ff2f827f487e83bda6173afa1234', 1, '', NULL, NOW(), NOW(), 'A', -1);

        -- RealDB tag (focus source — 值与 FocusSourceId 匹配)
        src1_no := 'PERF' || LPAD((seq * 2)::TEXT, 7, '0');
        INSERT INTO ssmcsourcemst (source_no, group_no, source_id, unit_nam, source_flag, source_cod, fstusr_dtm, lstusr_dtm, valid_sta, org_no)
        VALUES (src1_no, mon_no, focus_id, '', 3, tag_name, NOW(), NOW(), 'A', -1);

        -- Static 辅助 source (用于 TriggerValueDefDic 匹配)
        -- MonitorCenter 通过 status_no 关联 ssmcstatusmst/ssmcstatusdtlmst 获取 TriggerValueDefDic
        -- 不再额外插入 source
    END LOOP;
END $$;


-- ========================================================================
-- 2e. PackageValue 打包值规则 × 10,000 (rule_flag = 5)
--     使用位与运算匹配 TriggerValueDefDic
--     focus_id → 打包值(long) → 遍历 MonitorStatusDefinitions 位匹配
-- ========================================================================

DO $$
DECLARE
    base_idx  INTEGER := 65000;
    total     INTEGER := 10000;
    i         INTEGER;
    seq       INTEGER;
    mon_no    VARCHAR(32);
    mon_id    VARCHAR(32);
    mon_nam   VARCHAR(64);
    tag_name  VARCHAR(64);
    focus_id  VARCHAR(64);
    refresh   INTEGER;
    prerule   VARCHAR(32);
    src1_no   VARCHAR(32);
    src2_no   VARCHAR(32);
    status_no VARCHAR(64);
BEGIN
    status_no := '88a2ff2f827f487e83bda61736ca3ad1';

    FOR i IN 0 .. (total - 1) LOOP
        seq      := base_idx + i;
        mon_no   := 'PERF' || LPAD(seq::TEXT, 7, '0');
        mon_id   := 'perf-pkg-' || LPAD(i::TEXT, 5, '0');
        mon_nam  := 'PerfPkg ' || LPAD(i::TEXT, 5, '0');
        tag_name := 'perf.tag.' || LPAD(seq::TEXT, 6, '0');
        focus_id := 'perf.pkg.focus.' || LPAD(seq::TEXT, 6, '0');
        refresh  := CASE (i % 4)
                      WHEN 0 THEN 5
                      WHEN 1 THEN 10
                      WHEN 2 THEN 30
                      ELSE 60
                    END;
        prerule  := CASE (i % 5)
                      WHEN 0 THEN 'PERF-PRERULE-A'
                      WHEN 1 THEN 'PERF-PRERULE-B'
                      WHEN 2 THEN 'PERF-PRERULE-C'
                      WHEN 3 THEN 'PERF-PRERULE-D'
                      ELSE 'PERF-PRERULE-E'
                    END;

        INSERT INTO ssmcitemmst (monitor_no, monitor_id, monitor_nam, monitor_dsc, status_no, refresh_cnt, source_no, enable_flag, prerule_no, rule_flag, node_no, manual_flag, stop_monitor_key, fail_limit, fstusr_dtm, lstusr_dtm, valid_sta, org_no)
        VALUES (mon_no, mon_id, mon_nam, '', status_no, refresh, focus_id, true, prerule, 5,
                '88a2ff2f827f487e83bda6173afa1234', 1, '', NULL, NOW(), NOW(), 'A', -1);

        src1_no := 'PERF' || LPAD((seq * 2)::TEXT, 7, '0');
        INSERT INTO ssmcsourcemst (source_no, group_no, source_id, unit_nam, source_flag, source_cod, fstusr_dtm, lstusr_dtm, valid_sta, org_no)
        VALUES (src1_no, mon_no, focus_id, '', 3, tag_name, NOW(), NOW(), 'A', -1);
    END LOOP;
END $$;


-- ========================================================================
-- 2f. WallTemperature 壁温监测 × 5,000 (rule_flag = 6)
--     temperature_tag: 温度测点  reference_tag: 参考温度
--     threshold + levels(LevelValue, DelayTime)
--     NOTE: WallTemperatureOpts 在 MonitorCenter 中可能存储在独立表
-- ========================================================================

DO $$
DECLARE
    base_idx  INTEGER := 75000;
    total     INTEGER := 5000;
    i         INTEGER;
    seq       INTEGER;
    mon_no    VARCHAR(32);
    mon_id    VARCHAR(32);
    mon_nam   VARCHAR(64);
    temp_tag  VARCHAR(64);
    ref_tag   VARCHAR(64);
    refresh   INTEGER;
    prerule   VARCHAR(32);
    src1_no   VARCHAR(32);
    src2_no   VARCHAR(32);
    src3_no   VARCHAR(32);
    status_no VARCHAR(64);
    threshold INTEGER;
BEGIN
    status_no := '88a2ff2f827f487e83bda61736ca3ad1';

    FOR i IN 0 .. (total - 1) LOOP
        seq       := base_idx + i;
        mon_no    := 'PERF' || LPAD(seq::TEXT, 7, '0');
        mon_id    := 'perf-wall-' || LPAD(i::TEXT, 5, '0');
        mon_nam   := 'PerfWall ' || LPAD(i::TEXT, 5, '0');
        temp_tag  := 'perf.wall.temp.' || LPAD(seq::TEXT, 6, '0');
        ref_tag   := 'perf.wall.ref.' || LPAD(seq::TEXT, 6, '0');
        refresh   := CASE (i % 3)
                       WHEN 0 THEN 10
                       WHEN 1 THEN 30
                       ELSE 60
                     END;
        prerule   := CASE (i % 5)
                       WHEN 0 THEN 'PERF-PRERULE-A'
                       WHEN 1 THEN 'PERF-PRERULE-B'
                       WHEN 2 THEN 'PERF-PRERULE-C'
                       WHEN 3 THEN 'PERF-PRERULE-D'
                       ELSE 'PERF-PRERULE-E'
                     END;
        threshold := 50 + (i % 40);           -- 50~89

        INSERT INTO ssmcitemmst (monitor_no, monitor_id, monitor_nam, monitor_dsc, status_no, refresh_cnt, source_no, enable_flag, prerule_no, rule_flag, node_no, manual_flag, stop_monitor_key, fail_limit, fstusr_dtm, lstusr_dtm, valid_sta, org_no)
        VALUES (mon_no, mon_id, mon_nam, '', status_no, refresh, temp_tag, true, prerule, 6,
                '88a2ff2f827f487e83bda6173afa1234', 1, '', NULL, NOW(), NOW(), 'A', -1);

        -- Temperature tag
        src1_no := 'PERF' || LPAD((seq * 3)::TEXT, 7, '0');
        INSERT INTO ssmcsourcemst (source_no, group_no, source_id, unit_nam, source_flag, source_cod, fstusr_dtm, lstusr_dtm, valid_sta, org_no)
        VALUES (src1_no, mon_no, temp_tag, '°C', 3, temp_tag, NOW(), NOW(), 'A', -1);

        -- Reference tag
        src2_no := 'PERF' || LPAD((seq * 3 + 1)::TEXT, 7, '0');
        INSERT INTO ssmcsourcemst (source_no, group_no, source_id, unit_nam, source_flag, source_cod, fstusr_dtm, lstusr_dtm, valid_sta, org_no)
        VALUES (src2_no, mon_no, ref_tag, '°C', 3, ref_tag, NOW(), NOW(), 'A', -1);

        -- Static threshold
        src3_no := 'PERF' || LPAD((seq * 3 + 2)::TEXT, 7, '0');
        INSERT INTO ssmcsourcemst (source_no, group_no, source_id, unit_nam, source_flag, source_cod, fstusr_dtm, lstusr_dtm, valid_sta, org_no)
        VALUES (src3_no, mon_no, 'thr_' || mon_id, '°C', 1, threshold::TEXT, NOW(), NOW(), 'A', -1);
    END LOOP;
END $$;


-- ========================================================================
-- 2g. InterfaceMonitoring 接口监控 × 5,000 (rule_flag = 7)
--     部分启用 ManualFlag 检查 + StopMonitor 关联
--     failure_count: 3~7
--     InterfaceMonitoring 有自己的抑制逻辑 (非前置规则系统)
-- ========================================================================

DO $$
DECLARE
    base_idx    INTEGER := 80000;
    total       INTEGER := 5000;
    i           INTEGER;
    seq         INTEGER;
    mon_no      VARCHAR(32);
    mon_id      VARCHAR(32);
    mon_nam     VARCHAR(64);
    tag_name    VARCHAR(64);
    refresh     INTEGER;
    manual_flag INTEGER;
    stop_key    VARCHAR(64);
    fail_limit  INTEGER;
    im_enabled  BOOLEAN;
    src1_no     VARCHAR(32);
    status_no   VARCHAR(64);
BEGIN
    status_no := '88a2ff2f827f487e83bda61736ca3ad1';

    FOR i IN 0 .. (total - 1) LOOP
        seq         := base_idx + i;
        mon_no      := 'PERF' || LPAD(seq::TEXT, 7, '0');
        mon_id      := 'perf-ifm-' || LPAD(i::TEXT, 5, '0');
        mon_nam     := 'PerfIfMon ' || LPAD(i::TEXT, 5, '0');
        tag_name    := 'perf.tag.' || LPAD(seq::TEXT, 6, '0');
        refresh     := CASE (i % 4)
                         WHEN 0 THEN 5
                         WHEN 1 THEN 10
                         WHEN 2 THEN 30
                         ELSE 60
                       END;
        -- 20% ManualFlag=0 (手动停止), 其余正常运行
        manual_flag := CASE WHEN (i % 5 = 0) THEN 0 ELSE 1 END;
        -- 20% 有关联 StopMonitor
        stop_key    := CASE WHEN (i % 5 = 1) THEN mon_no ELSE '' END;
        fail_limit  := 3 + (i % 5);          -- 3~7
        im_enabled  := (i % 10 != 9);         -- 90% 启用 InterfaceMonitoring 抑制

        INSERT INTO ssmcitemmst (monitor_no, monitor_id, monitor_nam, monitor_dsc, status_no, refresh_cnt, source_no, enable_flag, prerule_no, rule_flag, node_no, manual_flag, stop_monitor_key, fail_limit, fstusr_dtm, lstusr_dtm, valid_sta, org_no)
        VALUES (mon_no, mon_id, mon_nam, '', status_no, refresh, tag_name, true, NULL, 7,
                '88a2ff2f827f487e83bda6173afa1234', manual_flag, stop_key, fail_limit, NOW(), NOW(), 'A', -1);
        -- NOTE: prerule_no = NULL → 仅使用 InterfaceMonitoring 自身抑制逻辑

        src1_no := 'PERF' || LPAD((seq * 2)::TEXT, 7, '0');
        INSERT INTO ssmcsourcemst (source_no, group_no, source_id, unit_nam, source_flag, source_cod, fstusr_dtm, lstusr_dtm, valid_sta, org_no)
        VALUES (src1_no, mon_no, tag_name, '%', 3, tag_name, NOW(), NOW(), 'A', -1);
    END LOOP;
END $$;


-- ========================================================================
-- 2h. RulePackageValue 多打包点规则 × 10,000 (rule_flag = 8)
--     每规则有 StartKey/EndKey 范围 + 位与运算
--     多个 MonitorStatusDefinitions，每个有 TriggerValueDefDic
-- ========================================================================

DO $$
DECLARE
    base_idx  INTEGER := 85000;
    total     INTEGER := 10000;
    i         INTEGER;
    seq       INTEGER;
    mon_no    VARCHAR(32);
    mon_id    VARCHAR(32);
    mon_nam   VARCHAR(64);
    tag_name  VARCHAR(64);
    focus_id  VARCHAR(64);
    refresh   INTEGER;
    prerule   VARCHAR(32);
    src1_no   VARCHAR(32);
    status_no VARCHAR(64);
BEGIN
    status_no := '88a2ff2f827f487e83bda61736ca3ad1';

    FOR i IN 0 .. (total - 1) LOOP
        seq      := base_idx + i;
        mon_no   := 'PERF' || LPAD(seq::TEXT, 7, '0');
        mon_id   := 'perf-rpkg-' || LPAD(i::TEXT, 5, '0');
        mon_nam  := 'PerfRPkg ' || LPAD(i::TEXT, 5, '0');
        tag_name := 'perf.tag.' || LPAD(seq::TEXT, 6, '0');
        focus_id := 'perf.rpkg.focus.' || LPAD(seq::TEXT, 6, '0');
        refresh  := CASE (i % 4)
                      WHEN 0 THEN 10
                      WHEN 1 THEN 30
                      WHEN 2 THEN 60
                      ELSE 120
                    END;
        prerule  := CASE (i % 5)
                      WHEN 0 THEN 'PERF-PRERULE-A'
                      WHEN 1 THEN 'PERF-PRERULE-B'
                      WHEN 2 THEN 'PERF-PRERULE-C'
                      WHEN 3 THEN 'PERF-PRERULE-D'
                      ELSE 'PERF-PRERULE-E'
                    END;

        INSERT INTO ssmcitemmst (monitor_no, monitor_id, monitor_nam, monitor_dsc, status_no, refresh_cnt, source_no, enable_flag, prerule_no, rule_flag, node_no, manual_flag, stop_monitor_key, fail_limit, fstusr_dtm, lstusr_dtm, valid_sta, org_no)
        VALUES (mon_no, mon_id, mon_nam, '', status_no, refresh, focus_id, true, prerule, 8,
                '88a2ff2f827f487e83bda6173afa1234', 1, '', NULL, NOW(), NOW(), 'A', -1);

        src1_no := 'PERF' || LPAD((seq * 2)::TEXT, 7, '0');
        INSERT INTO ssmcsourcemst (source_no, group_no, source_id, unit_nam, source_flag, source_cod, fstusr_dtm, lstusr_dtm, valid_sta, org_no)
        VALUES (src1_no, mon_no, focus_id, '', 3, tag_name, NOW(), NOW(), 'A', -1);
    END LOOP;
END $$;


-- ========================================================================
-- 2i. MultiStateRangeDuration 多状态区间时长 × 5,000 (rule_flag = 9)
--     每监视项多个状态条件，每个条件有独立 DurationSecond 累计
--     tag 值在不同的范围内触发不同状态
-- ========================================================================

DO $$
DECLARE
    base_idx  INTEGER := 95000;
    total     INTEGER := 5000;
    i         INTEGER;
    seq       INTEGER;
    mon_no    VARCHAR(32);
    mon_id    VARCHAR(32);
    mon_nam   VARCHAR(64);
    tag_name  VARCHAR(64);
    refresh   INTEGER;
    prerule   VARCHAR(32);
    src1_no   VARCHAR(32);
    status_no VARCHAR(64);
BEGIN
    status_no := '88a2ff2f827f487e83bda61736ca3ad1';

    FOR i IN 0 .. (total - 1) LOOP
        seq      := base_idx + i;
        mon_no   := 'PERF' || LPAD(seq::TEXT, 7, '0');
        mon_id   := 'perf-mst-' || LPAD(i::TEXT, 5, '0');
        mon_nam  := 'PerfMultiSt ' || LPAD(i::TEXT, 5, '0');
        tag_name := 'perf.tag.' || LPAD(seq::TEXT, 6, '0');
        refresh  := CASE (i % 4)
                      WHEN 0 THEN 5
                      WHEN 1 THEN 10
                      WHEN 2 THEN 30
                      ELSE 60
                    END;
        prerule  := CASE (i % 5)
                      WHEN 0 THEN 'PERF-PRERULE-A'
                      WHEN 1 THEN 'PERF-PRERULE-B'
                      WHEN 2 THEN 'PERF-PRERULE-C'
                      WHEN 3 THEN 'PERF-PRERULE-D'
                      ELSE 'PERF-PRERULE-E'
                    END;

        INSERT INTO ssmcitemmst (monitor_no, monitor_id, monitor_nam, monitor_dsc, status_no, refresh_cnt, source_no, enable_flag, prerule_no, rule_flag, node_no, manual_flag, stop_monitor_key, fail_limit, fstusr_dtm, lstusr_dtm, valid_sta, org_no)
        VALUES (mon_no, mon_id, mon_nam, '', status_no, refresh, tag_name, true, prerule, 9,
                '88a2ff2f827f487e83bda6173afa1234', 1, '', NULL, NOW(), NOW(), 'A', -1);

        src1_no := 'PERF' || LPAD((seq * 2)::TEXT, 7, '0');
        INSERT INTO ssmcsourcemst (source_no, group_no, source_id, unit_nam, source_flag, source_cod, fstusr_dtm, lstusr_dtm, valid_sta, org_no)
        VALUES (src1_no, mon_no, tag_name, '%', 3, tag_name, NOW(), NOW(), 'A', -1);
    END LOOP;
END $$;


-- ============================================================================
-- 3. 数据完整性验证
-- ============================================================================

-- 3a. 总数检查
SELECT 'ssmcitemmst (monitors)'     AS tbl, COUNT(*) AS cnt FROM ssmcitemmst    WHERE monitor_no LIKE 'PERF%'
UNION ALL
SELECT 'ssmcsourcemst (sources)',   COUNT(*) FROM ssmcsourcemst     WHERE group_no   LIKE 'PERF%'
UNION ALL
SELECT 'ssmcrulerandurmst (rdur)',  COUNT(*) FROM ssmcrulerandurmst WHERE related_no LIKE 'PERF%'
UNION ALL
SELECT 'ssmcrulecodmst (expr)',     COUNT(*) FROM ssmcrulecodmst    WHERE related_no LIKE 'PERF%'
UNION ALL
SELECT 'ssmcprerulemst (prerules)', COUNT(*) FROM ssmcprerulemst    WHERE prerule_no LIKE 'PERF-PRERULE%';

-- 期望:
--   ssmcitemmst:             100,000
--   ssmcsourcemst:          ~230,000 (Expression 2×30K + RDur 2×15K + RFreq 2×10K
--                                      + Feat 1×10K + Pkg 1×10K + Wall 3×5K
--                                      + IfMon 1×5K + RPkg 1×10K + Multi 1×5K)
--   ssmcrulerandurmst:       ~25,000 (15K RDur + 10K RFreq)
--   ssmcrulecodmst:          ~30,000 (30K Expression)
--   ssmcprerulemst:               5

-- 3b. 按规则类型分布
SELECT rule_flag,
       CASE rule_flag
         WHEN 1 THEN 'Expression'
         WHEN 2 THEN 'RangeDuration'
         WHEN 3 THEN 'RangeFrequency'
         WHEN 4 THEN 'FeatureValue'
         WHEN 5 THEN 'PackageValue'
         WHEN 6 THEN 'WallTemperature'
         WHEN 7 THEN 'InterfaceMonitoring'
         WHEN 8 THEN 'RulePackageValue'
         WHEN 9 THEN 'MultiStateRangeDuration'
         ELSE 'UNKNOWN'
       END AS rule_name,
       COUNT(*) AS cnt
FROM ssmcitemmst
WHERE monitor_no LIKE 'PERF%'
GROUP BY rule_flag
ORDER BY rule_flag;

-- 3c. 按刷新间隔分布
SELECT refresh_cnt, COUNT(*) AS cnt
FROM ssmcitemmst
WHERE monitor_no LIKE 'PERF%'
GROUP BY refresh_cnt
ORDER BY refresh_cnt;

-- 3d. 按前置规则分布
SELECT COALESCE(prerule_no, '(none)') AS prerule, COUNT(*) AS cnt
FROM ssmcitemmst
WHERE monitor_no LIKE 'PERF%'
GROUP BY prerule_no
ORDER BY cnt DESC;

-- 3e. 检查缺少数据源的监视项
SELECT m.monitor_no, m.rule_flag,
       (SELECT COUNT(*) FROM ssmcsourcemst s WHERE s.group_no = m.monitor_no) AS src_count
FROM ssmcitemmst m
WHERE m.monitor_no LIKE 'PERF%'
  AND (SELECT COUNT(*) FROM ssmcsourcemst s WHERE s.group_no = m.monitor_no) = 0;
-- 期望: 0 rows

-- 3f. 检查 Expression 类型缺少表达式的监视项
SELECT m.monitor_no, m.rule_flag
FROM ssmcitemmst m
WHERE m.monitor_no LIKE 'PERF%'
  AND m.rule_flag = 1
  AND (SELECT COUNT(*) FROM ssmcrulecodmst c WHERE c.related_no = m.monitor_no) = 0;
-- 期望: 0 rows

-- 3g. 检查 RangeDuration/RangeFrequency 类型缺少规则的监视项
SELECT m.monitor_no, m.rule_flag
FROM ssmcitemmst m
WHERE m.monitor_no LIKE 'PERF%'
  AND m.rule_flag IN (2, 3)
  AND (SELECT COUNT(*) FROM ssmcrulerandurmst r WHERE r.related_no = m.monitor_no) = 0;
-- 期望: 0 rows

-- ============================================================================
-- 4. 准备运行
-- ============================================================================
-- 1. 确保 TrendDB 中 db05.test1 可读（或使用 SimulatedTrendReader）
-- 2. 确保 Redis + ClickHouse 健康: docker compose ps
-- 3. 启动 Worker (横向扩展): docker compose up -d --scale worker=5
-- 4. 触发全量同步:
--    curl -X POST http://localhost:11082/api/ruleengine/sync/full \
--      -H "Content-Type: application/json" \
--      -d '{"monitors":[],"version":"2026-07-19T00:00:00Z"}'
--    (实际同步由 MonitorCenter → Master 自动完成)
-- 5. 观察性能:
--    docker compose logs -f worker | grep -E "周期|偏慢|异常|elapsed"
--    docker compose logs -f master | grep -E "分区|Worker|推送"
-- 6. 检查 Redis 活跃报警:
--    docker compose exec redis redis-cli SCARD ruleengine:active_alarms
-- ============================================================================
