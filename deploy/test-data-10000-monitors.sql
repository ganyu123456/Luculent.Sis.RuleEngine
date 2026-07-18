-- ===== SIS RuleEngine: 10,000 监视项 + 前置规则测试数据 (PostgreSQL) =====
-- 统一数据源: db05.test1 (唯一实时趋势测点)
-- 阈值差异化: 30~90 (实现差异化触发)
-- Duration 差异化: 1~60s (实现错峰触发)
-- 前置规则: PRERULE-A (Expression) + PRERULE-B (RangeDuration)
-- 关联: 监视项 0-4999 → PRERULE-A, 5000-9999 → PRERULE-B
-- org_no: -1 (ABP 多租户一致性)

-- ===== 清理旧测试数据 =====
DELETE FROM ssmcrulerandurmst WHERE related_no LIKE 'MON%';
DELETE FROM ssmcrulecodmst WHERE related_no LIKE 'PRERULE-%';
DELETE FROM ssmcsourcemst WHERE group_no LIKE 'MON%' OR group_no LIKE 'PRERULE-%';
DELETE FROM ssmcitemmst WHERE monitor_no LIKE 'MON%';
DELETE FROM ssmcprerulemst WHERE prerule_no LIKE 'PRERULE-%';

-- ===== 1. 创建 2 个前置规则 =====

-- 前置规则 A: Expression 类型 — 条件: prerule_val > prerule_thr (db05.test1 > 50)
INSERT INTO ssmcprerulemst (prerule_no, prerule_nam, prerule_dsc, refresh_cnt, source_no, enable_flag, rule_flag, fstusr_dtm, lstusr_dtm, valid_sta, org_no)
VALUES ('PRERULE-A', 'Prerule A - Expression', 'db05.test1 > 50', 60, '', true, 1, NOW(), NOW(), 'A', -1);

-- 前置规则 B: RangeDuration 类型 — 条件: prerule_val >= prerule_thr 持续 0s (立即生效)
INSERT INTO ssmcprerulemst (prerule_no, prerule_nam, prerule_dsc, refresh_cnt, source_no, enable_flag, rule_flag, fstusr_dtm, lstusr_dtm, valid_sta, org_no)
VALUES ('PRERULE-B', 'Prerule B - RangeDuration', 'db05.test1 >= 50 for 0s', 60, '', true, 2, NOW(), NOW(), 'A', -1);

-- 前置规则 A 的数据源 (Expression)
INSERT INTO ssmcsourcemst (source_no, group_no, source_id, unit_nam, source_flag, source_cod, fstusr_dtm, lstusr_dtm, valid_sta, org_no)
VALUES
('PRERULE-A-SRC1', 'PRERULE-A', 'prerule_val', '',  3, 'db05.test1', NOW(), NOW(), 'A', -1),
('PRERULE-A-SRC2', 'PRERULE-A', 'prerule_thr', '',  1, '50',         NOW(), NOW(), 'A', -1);

-- 前置规则 B 的数据源 (RangeDuration)
INSERT INTO ssmcsourcemst (source_no, group_no, source_id, unit_nam, source_flag, source_cod, fstusr_dtm, lstusr_dtm, valid_sta, org_no)
VALUES
('PRERULE-B-SRC1', 'PRERULE-B', 'prerule_val', '',  3, 'db05.test1', NOW(), NOW(), 'A', -1),
('PRERULE-B-SRC2', 'PRERULE-B', 'prerule_thr', '',  1, '50',         NOW(), NOW(), 'A', -1);

-- 前置规则 A 的表达式: prerule_val > prerule_thr
INSERT INTO ssmcrulecodmst (cod_no, cod_cod, status_no, related_no, fstusr_dtm, lstusr_dtm, valid_sta, org_no)
VALUES ('PRERULE-A-EXP', 'prerule_val > prerule_thr', '39edc1419d86d38933a57f1e156b4991', 'PRERULE-A', NOW(), NOW(), 'A', -1);

-- 前置规则 B 的 RangeDuration 规则: prerule_val >= prerule_thr, duration=0
INSERT INTO ssmcrulerandurmst (ridur_no, statuslin_cod, status_no, related_no, enable_flag, left_id, symbol_flag, right_id, duration_cnt, fstusr_dtm, lstusr_dtm, valid_sta, org_no)
VALUES ('PRERULE-B-RULE', 'ok', '88a2ff2f827f487e83bda61736ca2b2d', 'PRERULE-B', true, 'prerule_val', 1, 'prerule_thr', 0, NOW(), NOW(), 'A', -1);

-- ===== 2. 批量创建 10,000 个监视项 =====

DO $$
DECLARE
    total INTEGER := 10000;
    i INTEGER := 0;
    monitor_no VARCHAR(40);
    monitor_id VARCHAR(64);
    monitor_nam VARCHAR(100);
    prerule_no VARCHAR(40);
    src1_no VARCHAR(40);
    src2_no VARCHAR(40);
    rule_no VARCHAR(40);
    threshold_val INTEGER;
    duration_val INTEGER;
BEGIN
    WHILE i < total LOOP
        monitor_no := 'MON' || LPAD((i + 1)::TEXT, 7, '0');
        monitor_id := 'mon-' || LPAD(i::TEXT, 7, '0');
        monitor_nam := 'Monitor ' || LPAD(i::TEXT, 5, '0');

        -- 前 5000 个关联 PRERULE-A，后 5000 个关联 PRERULE-B
        IF i < 5000 THEN
            prerule_no := 'PRERULE-A';
        ELSE
            prerule_no := 'PRERULE-B';
        END IF;

        -- 阈值差异化: 30~90，避免所有监视项同时触发
        threshold_val := 30 + (i % 61);
        -- Duration 差异化: 1~60s，错峰触发
        duration_val := 1 + (i % 60);

        -- MonitorItem
        INSERT INTO ssmcitemmst (monitor_no, monitor_id, monitor_nam, monitor_dsc, status_no, refresh_cnt, source_no, enable_flag, prerule_no, rule_flag, node_no, manual_flag, stop_monitor_key, fail_limit, fstusr_dtm, lstusr_dtm, valid_sta, org_no)
        VALUES (monitor_no, monitor_id, monitor_nam, '', '88a2ff2f827f487e83bda61736ca3ad1', 1, '', true, prerule_no, 2, '88a2ff2f827f487e83bda6173afa1234', 1, '', NULL, NOW(), NOW(), 'A', -1);

        -- 数据源 1: RealDB → db05.test1 (source_flag=3)
        src1_no := 'SRC' || LPAD((i * 2 + 100000)::TEXT, 7, '0');
        INSERT INTO ssmcsourcemst (source_no, group_no, source_id, unit_nam, source_flag, source_cod, fstusr_dtm, lstusr_dtm, valid_sta, org_no)
        VALUES (src1_no, monitor_no, 'tag_value', '%', 3, 'db05.test1', NOW(), NOW(), 'A', -1);

        -- 数据源 2: Static 阈值 (source_flag=1)
        src2_no := 'SRC' || LPAD((i * 2 + 100001)::TEXT, 7, '0');
        INSERT INTO ssmcsourcemst (source_no, group_no, source_id, unit_nam, source_flag, source_cod, fstusr_dtm, lstusr_dtm, valid_sta, org_no)
        VALUES (src2_no, monitor_no, 'threshold', '', 1, threshold_val::TEXT, NOW(), NOW(), 'A', -1);

        -- RangeDuration 规则: tag_value > threshold, DurationSecond 差异化
        rule_no := 'RUL' || LPAD((i + 200000)::TEXT, 7, '0');
        INSERT INTO ssmcrulerandurmst (ridur_no, statuslin_cod, status_no, related_no, enable_flag, left_id, symbol_flag, right_id, duration_cnt, fstusr_dtm, lstusr_dtm, valid_sta, org_no)
        VALUES (rule_no, 'satisfiled', '88a2ff2f827f487e83bda61736ca3ad1', monitor_no, true, 'tag_value', 1, 'threshold', duration_val, NOW(), NOW(), 'A', -1);

        i := i + 1;
    END LOOP;
END $$;

-- ===== 验证 =====
SELECT 'monitors' AS tbl, COUNT(*) AS cnt FROM ssmcitemmst WHERE monitor_no LIKE 'MON%'
UNION ALL
SELECT 'sources', COUNT(*) FROM ssmcsourcemst WHERE group_no LIKE 'MON%' OR group_no LIKE 'PRERULE-%'
UNION ALL
SELECT 'rules', COUNT(*) FROM ssmcrulerandurmst WHERE related_no LIKE 'MON%' OR related_no LIKE 'PRERULE-%'
UNION ALL
SELECT 'prerules', COUNT(*) FROM ssmcprerulemst WHERE prerule_no LIKE 'PRERULE-%';

-- 验证所有监视项都使用 db05.test1
SELECT source_cod, COUNT(*) AS cnt
FROM ssmcsourcemst
WHERE group_no LIKE 'MON%' AND source_flag = 3 AND valid_sta = 'A'
GROUP BY source_cod;

-- 验证阈值分布
SELECT source_cod::INTEGER AS threshold, COUNT(*) AS cnt
FROM ssmcsourcemst
WHERE group_no LIKE 'MON%' AND source_flag = 1 AND valid_sta = 'A'
GROUP BY source_cod::INTEGER
ORDER BY threshold
LIMIT 10;
