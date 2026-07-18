-- ===== SIS MonitorCenter: 1000 监视项 + 前置规则测试数据 (PostgreSQL) =====
-- 使用前: 确保 ssmcstatusmst 已有 MonitorStatus 记录
-- 关联: 500 监视项 → prerule-A (Expression), 500 监视项 → prerule-B (RangeDuration)
-- 每个监视项: 2 个数据源 (RealDB + Static) + 1 个 RangeDuration 规则

-- ===== 清理旧测试数据 =====
DELETE FROM ssmcrulerandurmst WHERE related_no LIKE 'TEST%';
DELETE FROM ssmcrulecodmst WHERE related_no LIKE 'TEST-PRERULE%';
DELETE FROM ssmcsourcemst WHERE group_no LIKE 'TEST%' OR group_no LIKE 'TEST-PRERULE%';
DELETE FROM ssmcitemmst WHERE monitor_no LIKE 'TEST%';
DELETE FROM ssmcprerulemst WHERE prerule_no LIKE 'TEST-PRERULE%';

-- ===== 1. 创建 2 个前置规则 =====

-- 前置规则 A: Expression 类型 — 条件: 实时值 > 阈值
INSERT INTO ssmcprerulemst (prerule_no, prerule_nam, prerule_dsc, refresh_cnt, source_no, enable_flag, rule_flag, fstusr_dtm, lstusr_dtm, valid_sta, org_no)
VALUES ('TEST-PRERULE-A', 'Prerule A - Expression', 'tag_value > threshold', 60, '', true, 1, NOW(), NOW(), 'A', -1);

-- 前置规则 B: RangeDuration 类型 — 条件: 实时值 >= 阈值持续 0s
INSERT INTO ssmcprerulemst (prerule_no, prerule_nam, prerule_dsc, refresh_cnt, source_no, enable_flag, rule_flag, fstusr_dtm, lstusr_dtm, valid_sta, org_no)
VALUES ('TEST-PRERULE-B', 'Prerule B - RangeDuration', 'tag_value >= threshold for 0s', 60, '', true, 2, NOW(), NOW(), 'A', -1);

-- 前置规则 A 的数据源 (Expression 用)
INSERT INTO ssmcsourcemst (source_no, group_no, source_id, unit_nam, source_flag, source_cod, fstusr_dtm, lstusr_dtm, valid_sta, org_no)
VALUES
('TEST-PRSRC-A1', 'TEST-PRERULE-A', 'prerule_val', '', 3, 'PRE.TAG001', NOW(), NOW(), 'A', -1),
('TEST-PRSRC-A2', 'TEST-PRERULE-A', 'prerule_thr', '', 1, '50', NOW(), NOW(), 'A', -1);

-- 前置规则 B 的数据源 (RangeDuration 用)
INSERT INTO ssmcsourcemst (source_no, group_no, source_id, unit_nam, source_flag, source_cod, fstusr_dtm, lstusr_dtm, valid_sta, org_no)
VALUES
('TEST-PRSRC-B1', 'TEST-PRERULE-B', 'prerule_val', '', 3, 'PRE.TAG002', NOW(), NOW(), 'A', -1),
('TEST-PRSRC-B2', 'TEST-PRERULE-B', 'prerule_thr', '', 1, '50', NOW(), NOW(), 'A', -1);

-- 前置规则 A 的表达式: prerule_val > prerule_thr
INSERT INTO ssmcrulecodmst (cod_no, cod_cod, status_no, related_no, fstusr_dtm, lstusr_dtm, valid_sta, org_no)
VALUES ('TEST-PREXP-A', 'prerule_val > prerule_thr', '39edc1419d86d38933a57f1e156b4991', 'TEST-PRERULE-A', NOW(), NOW(), 'A', -1);

-- 前置规则 B 的 RangeDuration 规则
INSERT INTO ssmcrulerandurmst (ridur_no, statuslin_cod, status_no, related_no, enable_flag, left_id, symbol_flag, right_id, duration_cnt, fstusr_dtm, lstusr_dtm, valid_sta, org_no)
VALUES ('TEST-PRRULE-B', 'ok', '88a2ff2f827f487e83bda61736ca2b2d', 'TEST-PRERULE-B', true, 'prerule_val', 1, 'prerule_thr', 0, NOW(), NOW(), 'A', -1);

-- ===== 2. 批量创建 1000 个监视项 =====

DO $$
DECLARE
    i INTEGER := 0;
    monitor_no VARCHAR(40);
    monitor_id VARCHAR(64);
    monitor_nam VARCHAR(100);
    prerule_no VARCHAR(40);
    source_no1 VARCHAR(40);
    source_no2 VARCHAR(40);
    tag_id VARCHAR(64);
    rule_no VARCHAR(40);
BEGIN
    WHILE i < 1000 LOOP
        monitor_no := 'TEST' || LPAD(i::TEXT, 7, '0');
        monitor_id := 'test-' || LPAD(i::TEXT, 4, '0');
        monitor_nam := 'Test Monitor ' || LPAD(i::TEXT, 4, '0');

        -- 前 500 个关联 prerule-A，后 500 个关联 prerule-B
        IF i < 500 THEN
            prerule_no := 'TEST-PRERULE-A';
        ELSE
            prerule_no := 'TEST-PRERULE-B';
        END IF;

        -- MonitorItem
        INSERT INTO ssmcitemmst (monitor_no, monitor_id, monitor_nam, monitor_dsc, status_no, refresh_cnt, source_no, enable_flag, prerule_no, rule_flag, node_no, manual_flag, stop_monitor_key, fail_limit, fstusr_dtm, lstusr_dtm, valid_sta, org_no)
        VALUES (monitor_no, monitor_id, monitor_nam, '', '88a2ff2f827f487e83bda61736ca3ad1', 30, '', true, prerule_no, 2, '88a2ff2f827f487e83bda6173afa1234', 1, '', NULL, NOW(), NOW(), 'A', -1);

        -- 数据源 1: RealDB tag
        source_no1 := 'TEST' || LPAD((i * 2)::TEXT, 7, '0');
        tag_id := 'SIM.TAG' || LPAD(i::TEXT, 4, '0');
        INSERT INTO ssmcsourcemst (source_no, group_no, source_id, unit_nam, source_flag, source_cod, fstusr_dtm, lstusr_dtm, valid_sta, org_no)
        VALUES (source_no1, monitor_no, tag_id, '%', 3, tag_id, NOW(), NOW(), 'A', -1);

        -- 数据源 2: Static threshold
        source_no2 := 'TEST' || LPAD((i * 2 + 1)::TEXT, 7, '0');
        INSERT INTO ssmcsourcemst (source_no, group_no, source_id, unit_nam, source_flag, source_cod, fstusr_dtm, lstusr_dtm, valid_sta, org_no)
        VALUES (source_no2, monitor_no, 'thr_' || monitor_id, '', 1, '50', NOW(), NOW(), 'A', -1);

        -- RangeDuration 规则: tag_value > threshold, DurationSecond=1 (1s 后触发)
        rule_no := 'TEST' || LPAD((i + 10000)::TEXT, 7, '0');
        INSERT INTO ssmcrulerandurmst (ridur_no, statuslin_cod, status_no, related_no, enable_flag, left_id, symbol_flag, right_id, duration_cnt, fstusr_dtm, lstusr_dtm, valid_sta, org_no)
        VALUES (rule_no, 'satisfiled', '88a2ff2f827f487e83bda61736ca3ad1', monitor_no, true, tag_id, 1, 'thr_' || monitor_id, 1, NOW(), NOW(), 'A', -1);

        i := i + 1;
    END LOOP;
END $$;

-- ===== 验证 =====
SELECT 'Monitors' AS tbl, COUNT(*) AS cnt FROM ssmcitemmst WHERE monitor_no LIKE 'TEST%'
UNION ALL
SELECT 'Sources', COUNT(*) FROM ssmcsourcemst WHERE group_no LIKE 'TEST%'
UNION ALL
SELECT 'Rules', COUNT(*) FROM ssmcrulerandurmst WHERE related_no LIKE 'TEST%'
UNION ALL
SELECT 'Prerules', COUNT(*) FROM ssmcprerulemst WHERE prerule_no LIKE 'TEST-PRERULE%';
