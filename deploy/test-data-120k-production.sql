-- ===== SIS RuleEngine: 120K 监视项生产模拟测试数据 (PostgreSQL) =====
-- 6 种规则类型 × 20K = 120K 监视项
-- 多样化测点: db01~db10 × 10种测量类型 × 999个设备
-- Tag 格式: db{01-10}.{measure_type}_{device_id:03d}
-- 组织: org_no = -1 (ABP 多租户)

-- ===== Phase 0: 清理所有现有数据 =====
DELETE FROM ssmcrulemulstarandurmst WHERE related_no LIKE 'MSRD-%' OR related_no LIKE 'RDUR-%' OR related_no LIKE 'EXPR-%' OR related_no LIKE 'FEAT-%' OR related_no LIKE 'PACK-%' OR related_no LIKE 'RPAC-%';
DELETE FROM ssmcrulepacvalmst WHERE related_no LIKE 'RPAC-%';
DELETE FROM ssmcrulerandurmst WHERE related_no LIKE 'RDUR-%' OR related_no LIKE 'PRERULE-%';
DELETE FROM ssmcrulecodmst WHERE related_no LIKE 'EXPR-%' OR related_no LIKE 'PRERULE-%';
DELETE FROM ssmcsourcemst WHERE group_no LIKE 'EXPR-%' OR group_no LIKE 'RDUR-%' OR group_no LIKE 'FEAT-%' OR group_no LIKE 'PACK-%' OR group_no LIKE 'RPAC-%' OR group_no LIKE 'MSRD-%' OR group_no LIKE 'PRERULE-%';
DELETE FROM ssmcstatuslin WHERE status_no LIKE 'STATUS-%';
DELETE FROM ssmcstatusmst WHERE status_no LIKE 'STATUS-%';
DELETE FROM ssmcitemmst WHERE monitor_no LIKE 'EXPR-%' OR monitor_no LIKE 'RDUR-%' OR monitor_no LIKE 'FEAT-%' OR monitor_no LIKE 'PACK-%' OR monitor_no LIKE 'RPAC-%' OR monitor_no LIKE 'MSRD-%';
DELETE FROM ssmcprerulemst WHERE prerule_no LIKE 'PRERULE-%';

-- 也清理旧的 MON 前缀数据
DELETE FROM ssmcrulerandurmst WHERE related_no LIKE 'MON%';
DELETE FROM ssmcrulecodmst WHERE related_no LIKE 'MON%';
DELETE FROM ssmcsourcemst WHERE group_no LIKE 'MON%';
DELETE FROM ssmcitemmst WHERE monitor_no LIKE 'MON%';

-- ===== Phase 1: 创建状态主题和状态定义 =====

-- 1a. Expression 表达式规则状态主题
INSERT INTO ssmcstatusmst (status_no, status_nam, appdef_flag, fstusr_dtm, lstusr_dtm, valid_sta, org_no)
VALUES ('STATUS-EXPR', '表达式规则状态主题', false, NOW(), NOW(), 'A', -1)
ON CONFLICT (status_no) DO UPDATE SET status_nam = EXCLUDED.status_nam, lstusr_dtm = NOW();

INSERT INTO ssmcstatuslin (statuslin_no, statuslin_nam, statuslin_dsc, statuslin_cod, statuslin_cnt, status_no, fstusr_dtm, lstusr_dtm, valid_sta, org_no)
VALUES ('STATLIN-EXPR-1', '表达式触发', '表达式条件满足', 'expression_triggered', 1, 'STATUS-EXPR', NOW(), NOW(), 'A', -1)
ON CONFLICT (statuslin_cod) DO NOTHING;

-- 1b. RangeDuration 区间时长规则状态主题
INSERT INTO ssmcstatusmst (status_no, status_nam, appdef_flag, fstusr_dtm, lstusr_dtm, valid_sta, org_no)
VALUES ('STATUS-RDUR', '区间时长规则状态主题', false, NOW(), NOW(), 'A', -1)
ON CONFLICT (status_no) DO UPDATE SET status_nam = EXCLUDED.status_nam, lstusr_dtm = NOW();

INSERT INTO ssmcstatuslin (statuslin_no, statuslin_nam, statuslin_dsc, statuslin_cod, statuslin_cnt, status_no, fstusr_dtm, lstusr_dtm, valid_sta, org_no)
VALUES
('STATLIN-RDUR-1', '满足条件', '区间时长条件满足', 'satisfiled', 1, 'STATUS-RDUR', NOW(), NOW(), 'A', -1),
('STATLIN-RDUR-2', '严重告警', '区间时长严重告警', 'severe', 2, 'STATUS-RDUR', NOW(), NOW(), 'A', -1)
ON CONFLICT (statuslin_cod) DO NOTHING;

-- 1c. FeatureValue 特征值规则状态主题
INSERT INTO ssmcstatusmst (status_no, status_nam, appdef_flag, fstusr_dtm, lstusr_dtm, valid_sta, org_no)
VALUES ('STATUS-FEAT', '特征值规则状态主题', false, NOW(), NOW(), 'A', -1)
ON CONFLICT (status_no) DO UPDATE SET status_nam = EXCLUDED.status_nam, lstusr_dtm = NOW();

-- 特征值状态定义: 不同的 bit value 对应不同状态
INSERT INTO ssmcstatuslin (statuslin_no, statuslin_nam, statuslin_dsc, statuslin_cod, statuslin_cnt, status_no, fstusr_dtm, lstusr_dtm, valid_sta, org_no, statuslin_trigger)
VALUES
('STATLIN-FEAT-1', '特征值1', '特征值匹配1', 'feature_val_1', 1, 'STATUS-FEAT', NOW(), NOW(), 'A', -1, 1),
('STATLIN-FEAT-2', '特征值2', '特征值匹配2', 'feature_val_2', 2, 'STATUS-FEAT', NOW(), NOW(), 'A', -1, 2),
('STATLIN-FEAT-3', '特征值3', '特征值匹配3', 'feature_val_3', 3, 'STATUS-FEAT', NOW(), NOW(), 'A', -1, 3)
ON CONFLICT (statuslin_cod) DO NOTHING;

-- 1d. PackageValue 打包点规则状态主题
INSERT INTO ssmcstatusmst (status_no, status_nam, appdef_flag, fstusr_dtm, lstusr_dtm, valid_sta, org_no)
VALUES ('STATUS-PACK', '打包点规则状态主题', false, NOW(), NOW(), 'A', -1)
ON CONFLICT (status_no) DO UPDATE SET status_nam = EXCLUDED.status_nam, lstusr_dtm = NOW();

INSERT INTO ssmcstatuslin (statuslin_no, statuslin_nam, statuslin_dsc, statuslin_cod, statuslin_cnt, status_no, fstusr_dtm, lstusr_dtm, valid_sta, org_no, statuslin_trigger)
VALUES
('STATLIN-PACK-1', '打包位1', '打包点位匹配1', 'pack_bit_1', 1, 'STATUS-PACK', NOW(), NOW(), 'A', -1, 1),
('STATLIN-PACK-2', '打包位2', '打包点位匹配2', 'pack_bit_2', 2, 'STATUS-PACK', NOW(), NOW(), 'A', -1, 2),
('STATLIN-PACK-3', '打包位3', '打包点位匹配3', 'pack_bit_3', 3, 'STATUS-PACK', NOW(), NOW(), 'A', -1, 4),
('STATLIN-PACK-4', '打包位4', '打包点位匹配4', 'pack_bit_4', 4, 'STATUS-PACK', NOW(), NOW(), 'A', -1, 8)
ON CONFLICT (statuslin_cod) DO NOTHING;

-- 1e. RulePackageValue 多打包点规则状态主题
INSERT INTO ssmcstatusmst (status_no, status_nam, appdef_flag, fstusr_dtm, lstusr_dtm, valid_sta, org_no)
VALUES ('STATUS-RPAC', '多打包点规则状态主题', false, NOW(), NOW(), 'A', -1)
ON CONFLICT (status_no) DO UPDATE SET status_nam = EXCLUDED.status_nam, lstusr_dtm = NOW();

INSERT INTO ssmcstatuslin (statuslin_no, statuslin_nam, statuslin_dsc, statuslin_cod, statuslin_cnt, status_no, fstusr_dtm, lstusr_dtm, valid_sta, org_no, statuslin_trigger)
VALUES
('STATLIN-RPAC-1', '多打包位1', '多打包位1匹配', 'multi_pack_1', 1, 'STATUS-RPAC', NOW(), NOW(), 'A', -1, 1),
('STATLIN-RPAC-2', '多打包位2', '多打包位2匹配', 'multi_pack_2', 2, 'STATUS-RPAC', NOW(), NOW(), 'A', -1, 2),
('STATLIN-RPAC-3', '多打包位3', '多打包位3匹配', 'multi_pack_3', 3, 'STATUS-RPAC', NOW(), NOW(), 'A', -1, 4)
ON CONFLICT (statuslin_cod) DO NOTHING;

-- 1f. RuleMultiStateRangeDuration 多区间时长规则状态主题
INSERT INTO ssmcstatusmst (status_no, status_nam, appdef_flag, fstusr_dtm, lstusr_dtm, valid_sta, org_no)
VALUES ('STATUS-MSRD', '多区间时长规则状态主题', false, NOW(), NOW(), 'A', -1)
ON CONFLICT (status_no) DO UPDATE SET status_nam = EXCLUDED.status_nam, lstusr_dtm = NOW();

INSERT INTO ssmcstatuslin (statuslin_no, statuslin_nam, statuslin_dsc, statuslin_cod, statuslin_cnt, status_no, fstusr_dtm, lstusr_dtm, valid_sta, org_no)
VALUES
('STATLIN-MSRD-1', '多状态警告', '多状态区间-警告', 'ms_warning', 1, 'STATUS-MSRD', NOW(), NOW(), 'A', -1),
('STATLIN-MSRD-2', '多状态严重', '多状态区间-严重', 'ms_severe', 2, 'STATUS-MSRD', NOW(), NOW(), 'A', -1),
('STATLIN-MSRD-3', '多状态紧急', '多状态区间-紧急', 'ms_critical', 3, 'STATUS-MSRD', NOW(), NOW(), 'A', -1)
ON CONFLICT (statuslin_cod) DO NOTHING;

-- ===== 辅助函数: 根据序号生成差异化 Tag 名 =====
-- Tag pools: 10 databases × 10 measure types × variable instances
-- db{01-10}.tag_{measure}_{device_id:03d}
-- measure types: temp, press, flow, level, vib, speed, curr, volt, power, freq
-- This gives ~10×10=100 tag pools, each with many device IDs

CREATE OR REPLACE FUNCTION gen_tag_name(i INTEGER, pool_offset INTEGER DEFAULT 0) RETURNS TEXT AS $$
DECLARE
    db_idx INTEGER;
    measure_idx INTEGER;
    device_id INTEGER;
    db_name TEXT;
    measure TEXT;
    measures TEXT[] := ARRAY['temp', 'press', 'flow', 'level', 'vib', 'speed', 'curr', 'volt', 'power', 'freq'];
BEGIN
    db_idx := ((i + pool_offset) % 10) + 1;
    measure_idx := ((i / 10 + pool_offset) % 10) + 1;
    device_id := (i % 999) + 1;
    db_name := 'db' || LPAD(db_idx::TEXT, 2, '0');
    RETURN db_name || '.tag_' || measures[measure_idx] || '_' || LPAD(device_id::TEXT, 3, '0');
END;
$$ LANGUAGE plpgsql;

-- ===== Phase 2: 生成 20K Expression 监视项 (表达式规则) =====
-- 表达式: tag_val > threshold (threshold varies 30-90)
-- Data sources: 2 per monitor (RealDB tag + Static threshold)
-- Rule: ssmcrulecodmst with expression

DO $$
DECLARE
    total INTEGER := 20000;
    i INTEGER := 0;
    mon_no VARCHAR(40);
    mon_id VARCHAR(64);
    mon_nam VARCHAR(100);
    src1_no VARCHAR(40);
    src2_no VARCHAR(40);
    src1_alias VARCHAR(64);
    src2_alias VARCHAR(64);
    rule_no VARCHAR(40);
    tag_name TEXT;
    threshold INTEGER;
    refresh_sec INTEGER;
    expr_script VARCHAR(2000);
    status_key VARCHAR(20);
BEGIN
    WHILE i < total LOOP
        mon_no := 'EXPR-' || LPAD(i::TEXT, 7, '0');
        mon_id := 'expr-mon-' || LPAD(i::TEXT, 7, '0');
        mon_nam := 'Expr Monitor ' || LPAD(i::TEXT, 5, '0');
        src1_no := 'EXPR-SRC1-' || LPAD(i::TEXT, 7, '0');
        src2_no := 'EXPR-SRC2-' || LPAD(i::TEXT, 7, '0');
        rule_no := 'EXPR-RULE-' || LPAD(i::TEXT, 7, '0');

        -- 多样化 tag 名 (pool 0)
        tag_name := gen_tag_name(i, 0);
        src1_alias := 'tag_val';
        src2_alias := 'threshold';

        -- 阈值差异化: 30-90，刷新间隔差异化: 1/5/10/30/60s
        threshold := 30 + (i % 61);
        refresh_sec := CASE WHEN (i % 5) = 0 THEN 1 WHEN (i % 5) = 1 THEN 5 WHEN (i % 5) = 2 THEN 10 WHEN (i % 5) = 3 THEN 30 ELSE 60 END;

        -- 表达式: 30% 使用 Math.* 函数
        IF (i % 10) < 3 THEN
            expr_script := 'Math.Abs(tag_val - threshold) > 10';
            status_key := 'expression_triggered';
        ELSIF (i % 10) < 5 THEN
            expr_script := 'Math.Max(tag_val, threshold) > 80';
            status_key := 'expression_triggered';
        ELSE
            expr_script := 'tag_val > threshold';
            status_key := 'expression_triggered';
        END IF;

        -- MonitorItem
        INSERT INTO ssmcitemmst (monitor_no, monitor_id, monitor_nam, monitor_dsc, status_no, refresh_cnt, source_no, enable_flag, prerule_no, rule_flag, node_no, manual_flag, fstusr_dtm, lstusr_dtm, valid_sta, org_no)
        VALUES (mon_no, mon_id, mon_nam, 'Expression rule monitor #' || i, 'STATUS-EXPR', refresh_sec, src1_no, true, NULL, 1, NULL, 1, NOW(), NOW(), 'A', -1);

        -- Data sources
        INSERT INTO ssmcsourcemst (source_no, group_no, source_id, unit_nam, source_flag, source_cod, fstusr_dtm, lstusr_dtm, valid_sta, org_no)
        VALUES
        (src1_no, mon_no, src1_alias, '%',  3, tag_name,         NOW(), NOW(), 'A', -1),
        (src2_no, mon_no, src2_alias, '',    1, threshold::TEXT,  NOW(), NOW(), 'A', -1);

        -- Expression rule
        INSERT INTO ssmcrulecodmst (cod_no, cod_cod, status_no, related_no, fstusr_dtm, lstusr_dtm, valid_sta, org_no)
        VALUES (rule_no, expr_script, 'STATUS-EXPR', mon_no, NOW(), NOW(), 'A', -1);

        i := i + 1;
    END LOOP;
END $$;

-- Expression monitors: 20000 done

-- ===== Phase 3: 生成 20K RangeDuration 监视项 (区间时长规则) =====
-- 规则: left_tag > right_threshold for N seconds
-- Data sources: 2 per monitor (left tag + right tag/threshold)

DO $$
DECLARE
    total INTEGER := 20000;
    i INTEGER := 0;
    mon_no VARCHAR(40);
    mon_id VARCHAR(64);
    mon_nam VARCHAR(100);
    src1_no VARCHAR(40);
    src2_no VARCHAR(40);
    rule_no VARCHAR(40);
    left_tag TEXT;
    right_tag TEXT;
    duration_sec INTEGER;
    refresh_sec INTEGER;
    symbol INTEGER;
BEGIN
    WHILE i < total LOOP
        mon_no := 'RDUR-' || LPAD(i::TEXT, 7, '0');
        mon_id := 'rdur-mon-' || LPAD(i::TEXT, 7, '0');
        mon_nam := 'RangeDuration Monitor ' || LPAD(i::TEXT, 5, '0');
        src1_no := 'RDUR-SRC1-' || LPAD(i::TEXT, 7, '0');
        src2_no := 'RDUR-SRC2-' || LPAD(i::TEXT, 7, '0');
        rule_no := 'RDUR-RULE-' || LPAD(i::TEXT, 7, '0');

        -- 多样化 tag 名 (pool 20000)
        left_tag := gen_tag_name(i, 20000);
        right_tag := gen_tag_name(i + 5000, 20000);

        -- Duration 差异化: 0-60s，错峰触发
        duration_sec := i % 61;
        refresh_sec := CASE WHEN (i % 5) = 0 THEN 1 WHEN (i % 5) = 1 THEN 5 WHEN (i % 5) = 2 THEN 10 WHEN (i % 5) = 3 THEN 30 ELSE 60 END;
        -- SymbolType: 25% each of Greater, GreaterOrEqual, Less, LessOrEqual
        symbol := CASE WHEN (i % 4) = 0 THEN 1 WHEN (i % 4) = 1 THEN 2 WHEN (i % 4) = 2 THEN 3 ELSE 4 END;

        -- 交替使用 realdb tag vs static threshold
        -- 60%: 两个 RealDB tag; 40%: 一个 RealDB tag + 一个 static threshold
        INSERT INTO ssmcitemmst (monitor_no, monitor_id, monitor_nam, monitor_dsc, status_no, refresh_cnt, source_no, enable_flag, prerule_no, rule_flag, node_no, manual_flag, fstusr_dtm, lstusr_dtm, valid_sta, org_no)
        VALUES (mon_no, mon_id, mon_nam, 'RangeDuration monitor #' || i, 'STATUS-RDUR', refresh_sec, src1_no, true, NULL, 2, NULL, 1, NOW(), NOW(), 'A', -1);

        IF (i % 10) < 6 THEN
            -- Both real tags (different pools)
            INSERT INTO ssmcsourcemst (source_no, group_no, source_id, unit_nam, source_flag, source_cod, fstusr_dtm, lstusr_dtm, valid_sta, org_no)
            VALUES
            (src1_no, mon_no, 'left_val',  'MPa', 3, left_tag,  NOW(), NOW(), 'A', -1),
            (src2_no, mon_no, 'right_val', 'MPa', 3, right_tag, NOW(), NOW(), 'A', -1);
        ELSE
            -- One real tag + one static
            INSERT INTO ssmcsourcemst (source_no, group_no, source_id, unit_nam, source_flag, source_cod, fstusr_dtm, lstusr_dtm, valid_sta, org_no)
            VALUES
            (src1_no, mon_no, 'left_val',  'MPa', 3, left_tag,   NOW(), NOW(), 'A', -1),
            (src2_no, mon_no, 'right_val', 'MPa', 1, ((30 + (i::integer % 61)))::TEXT, NOW(), NOW(), 'A', -1);
        END IF;

        -- RangeDuration rule: 50% satisfiled, 50% severe
        INSERT INTO ssmcrulerandurmst (ridur_no, statuslin_cod, status_no, related_no, enable_flag, left_id, symbol_flag, right_id, duration_cnt, fstusr_dtm, lstusr_dtm, valid_sta, org_no)
        VALUES (rule_no, CASE WHEN (i % 2) = 0 THEN 'satisfiled' ELSE 'severe' END, 'STATUS-RDUR', mon_no, true, 'left_val', symbol, 'right_val', duration_sec, NOW(), NOW(), 'A', -1);

        i := i + 1;
    END LOOP;
END $$;

-- RangeDuration monitors: 20000 done

-- ===== Phase 4: 生成 20K FeatureValue 监视项 (特征值规则) =====
-- FeatureValue uses FocusSourceId + TriggerValueDefDic
-- Status lines carry trigger values (statuslin_trigger = 1, 2, 3)
-- MonitorStatusDefinitions.TriggerValueDefDic = {1: "feature_val_1", 2: "feature_val_2", 3: "feature_val_3"}

DO $$
DECLARE
    total INTEGER := 20000;
    i INTEGER := 0;
    mon_no VARCHAR(40);
    mon_id VARCHAR(64);
    mon_nam VARCHAR(100);
    src1_no VARCHAR(40);
    tag_name TEXT;
    refresh_sec INTEGER;
BEGIN
    WHILE i < total LOOP
        mon_no := 'FEAT-' || LPAD(i::TEXT, 7, '0');
        mon_id := 'feat-mon-' || LPAD(i::TEXT, 7, '0');
        mon_nam := 'FeatureValue Monitor ' || LPAD(i::TEXT, 5, '0');
        src1_no := 'FEAT-SRC1-' || LPAD(i::TEXT, 7, '0');

        -- 多样化 tag 名 (pool 40000)
        tag_name := gen_tag_name(i, 40000);
        refresh_sec := CASE WHEN (i % 5) = 0 THEN 1 WHEN (i % 5) = 1 THEN 5 WHEN (i % 5) = 2 THEN 10 WHEN (i % 5) = 3 THEN 30 ELSE 60 END;

        -- MonitorItem: RuleType=4 (FeatureValue)
        -- FocusSourceId = src1_no, which maps to the source's Key (alias)
        INSERT INTO ssmcitemmst (monitor_no, monitor_id, monitor_nam, monitor_dsc, status_no, refresh_cnt, source_no, enable_flag, prerule_no, rule_flag, node_no, manual_flag, fstusr_dtm, lstusr_dtm, valid_sta, org_no)
        VALUES (mon_no, mon_id, mon_nam, 'FeatureValue monitor #' || i, 'STATUS-FEAT', refresh_sec, src1_no, true, NULL, 4, NULL, 1, NOW(), NOW(), 'A', -1);

        -- Data source: use 'feat_' prefix in alias so SimulatedTrendReader generates discrete values (1,2,3)
        INSERT INTO ssmcsourcemst (source_no, group_no, source_id, unit_nam, source_flag, source_cod, fstusr_dtm, lstusr_dtm, valid_sta, org_no)
        VALUES (src1_no, mon_no, 'feat_' || tag_name, '', 3, tag_name, NOW(), NOW(), 'A', -1);

        i := i + 1;
    END LOOP;
END $$;

-- FeatureValue monitors: 20000 done

-- ===== Phase 5: 生成 20K PackageValue 监视项 (打包点规则) =====
-- PackageValue uses FocusSourceId + TriggerValueDefDic with bit positions

DO $$
DECLARE
    total INTEGER := 20000;
    i INTEGER := 0;
    mon_no VARCHAR(40);
    mon_id VARCHAR(64);
    mon_nam VARCHAR(100);
    src1_no VARCHAR(40);
    tag_name TEXT;
    refresh_sec INTEGER;
BEGIN
    WHILE i < total LOOP
        mon_no := 'PACK-' || LPAD(i::TEXT, 7, '0');
        mon_id := 'pack-mon-' || LPAD(i::TEXT, 7, '0');
        mon_nam := 'PackageValue Monitor ' || LPAD(i::TEXT, 5, '0');
        src1_no := 'PACK-SRC1-' || LPAD(i::TEXT, 7, '0');

        -- 多样化 tag 名 (pool 60000)
        tag_name := gen_tag_name(i, 60000);
        refresh_sec := CASE WHEN (i % 5) = 0 THEN 1 WHEN (i % 5) = 1 THEN 5 WHEN (i % 5) = 2 THEN 10 WHEN (i % 5) = 3 THEN 30 ELSE 60 END;

        INSERT INTO ssmcitemmst (monitor_no, monitor_id, monitor_nam, monitor_dsc, status_no, refresh_cnt, source_no, enable_flag, prerule_no, rule_flag, node_no, manual_flag, fstusr_dtm, lstusr_dtm, valid_sta, org_no)
        VALUES (mon_no, mon_id, mon_nam, 'PackageValue monitor #' || i, 'STATUS-PACK', refresh_sec, src1_no, true, NULL, 5, NULL, 1, NOW(), NOW(), 'A', -1);

        INSERT INTO ssmcsourcemst (source_no, group_no, source_id, unit_nam, source_flag, source_cod, fstusr_dtm, lstusr_dtm, valid_sta, org_no)
        VALUES (src1_no, mon_no, 'pack_src', '', 3, tag_name, NOW(), NOW(), 'A', -1);

        i := i + 1;
    END LOOP;
END $$;

-- PackageValue monitors: 20000 done

-- ===== Phase 6: 生成 20K RulePackageValue 监视项 (多打包点规则) =====
-- Uses ssmcrulepacvalmst table with SourceKey, StartKey, EndKey

DO $$
DECLARE
    total INTEGER := 20000;
    i INTEGER := 0;
    mon_no VARCHAR(40);
    mon_id VARCHAR(64);
    mon_nam VARCHAR(100);
    src1_no VARCHAR(40);
    src2_no VARCHAR(40);
    rule_no VARCHAR(40);
    tag_name TEXT;
    threshold_name TEXT;
    refresh_sec INTEGER;
    start_bit INTEGER;
    end_bit INTEGER;
BEGIN
    WHILE i < total LOOP
        mon_no := 'RPAC-' || LPAD(i::TEXT, 7, '0');
        mon_id := 'rpac-mon-' || LPAD(i::TEXT, 7, '0');
        mon_nam := 'RulePackageValue Monitor ' || LPAD(i::TEXT, 5, '0');
        src1_no := 'RPAC-SRC1-' || LPAD(i::TEXT, 7, '0');
        src2_no := 'RPAC-SRC2-' || LPAD(i::TEXT, 7, '0');
        rule_no := 'RPAC-RULE-' || LPAD(i::TEXT, 7, '0');

        -- 多样化 tag 名 (pool 80000)
        tag_name := gen_tag_name(i, 80000);
        threshold_name := gen_tag_name(i + 3000, 80000);

        refresh_sec := CASE WHEN (i % 5) = 0 THEN 1 WHEN (i % 5) = 1 THEN 5 WHEN (i % 5) = 2 THEN 10 WHEN (i % 5) = 3 THEN 30 ELSE 60 END;

        -- StartKey/EndKey: bit range varies 1-16
        start_bit := (i % 8) + 1;
        end_bit := start_bit + (i % 4) + 1;
        IF end_bit > 16 THEN end_bit := 16; END IF;

        INSERT INTO ssmcitemmst (monitor_no, monitor_id, monitor_nam, monitor_dsc, status_no, refresh_cnt, source_no, enable_flag, prerule_no, rule_flag, node_no, manual_flag, fstusr_dtm, lstusr_dtm, valid_sta, org_no)
        VALUES (mon_no, mon_id, mon_nam, 'RulePackageValue monitor #' || i, 'STATUS-RPAC', refresh_sec, src1_no, true, NULL, 8, NULL, 1, NOW(), NOW(), 'A', -1);

        INSERT INTO ssmcsourcemst (source_no, group_no, source_id, unit_nam, source_flag, source_cod, fstusr_dtm, lstusr_dtm, valid_sta, org_no)
        VALUES
        (src1_no, mon_no, 'rpac_val', '', 3, tag_name,      NOW(), NOW(), 'A', -1),
        (src2_no, mon_no, 'rpac_ref', '', 3, threshold_name, NOW(), NOW(), 'A', -1);

        -- RulePackageValue rule
        INSERT INTO ssmcrulepacvalmst (paval_no, status_no, related_no, enable_flag, source_id, start_id, end_id, fstusr_dtm, lstusr_dtm, valid_sta, org_no, statuslin_cnt)
        VALUES (rule_no, 'STATUS-RPAC', mon_no, true, 'rpac_val', start_bit::TEXT, end_bit::TEXT, NOW(), NOW(), 'A', -1, 1);

        i := i + 1;
    END LOOP;
END $$;

-- RulePackageValue monitors: 20000 done

-- ===== Phase 7: 生成 20K MultiStateRangeDuration 监视项 (多区间时长规则) =====
-- Uses ssmcrulemulstarandurmst with multiple conditions

DO $$
DECLARE
    total INTEGER := 20000;
    i INTEGER := 0;
    mon_no VARCHAR(40);
    mon_id VARCHAR(64);
    mon_nam VARCHAR(100);
    src1_no VARCHAR(40);
    rule1_no VARCHAR(40);
    rule2_no VARCHAR(40);
    tag_name TEXT;
    refresh_sec INTEGER;
    duration1 INTEGER;
    duration2 INTEGER;
    threshold1 INTEGER;
    threshold2 INTEGER;
BEGIN
    WHILE i < total LOOP
        mon_no := 'MSRD-' || LPAD(i::TEXT, 7, '0');
        mon_id := 'msrd-mon-' || LPAD(i::TEXT, 7, '0');
        mon_nam := 'MultiStateRangeDuration Monitor ' || LPAD(i::TEXT, 5, '0');
        src1_no := 'MSRD-SRC1-' || LPAD(i::TEXT, 7, '0');
        rule1_no := 'MSRD-RULE1-' || LPAD(i::TEXT, 7, '0');
        rule2_no := 'MSRD-RULE2-' || LPAD(i::TEXT, 7, '0');

        -- 多样化 tag 名 (pool 100000)
        tag_name := gen_tag_name(i, 100000);

        refresh_sec := CASE WHEN (i % 5) = 0 THEN 1 WHEN (i % 5) = 1 THEN 5 WHEN (i % 5) = 2 THEN 10 WHEN (i % 5) = 3 THEN 30 ELSE 60 END;
        duration1 := i % 31;
        duration2 := (i % 61);
        -- 数值阈值差异化: 30-90 范围
        threshold1 := 30 + (i % 31);
        threshold2 := 50 + (i % 41);

        INSERT INTO ssmcitemmst (monitor_no, monitor_id, monitor_nam, monitor_dsc, status_no, refresh_cnt, source_no, enable_flag, prerule_no, rule_flag, node_no, manual_flag, fstusr_dtm, lstusr_dtm, valid_sta, org_no)
        VALUES (mon_no, mon_id, mon_nam, 'MultiStateRangeDuration monitor #' || i, 'STATUS-MSRD', refresh_sec, src1_no, true, NULL, 9, NULL, 1, NOW(), NOW(), 'A', -1);

        -- 单个数据源: tag value (right_id 改为数值阈值，供计算器 CompareSymbol 直接比对)
        INSERT INTO ssmcsourcemst (source_no, group_no, source_id, unit_nam, source_flag, source_cod, fstusr_dtm, lstusr_dtm, valid_sta, org_no)
        VALUES
        (src1_no, mon_no, 'ms_val', '°C', 3, tag_name, NOW(), NOW(), 'A', -1);

        -- Multi-state conditions: right_id 为数值阈值 (计算器 RightValue 直接解析)
        INSERT INTO ssmcrulemulstarandurmst (ridur_no, statuslin_cod, status_no, related_no, enable_flag, left_id, symbol_flag, right_id, duration_cnt, fstusr_dtm, lstusr_dtm, valid_sta, org_no)
        VALUES
        (rule1_no, 'ms_warning', 'STATUS-MSRD', mon_no, true, 'ms_val', 1, threshold1::TEXT, duration1, NOW(), NOW(), 'A', -1),
        (rule2_no, 'ms_severe',  'STATUS-MSRD', mon_no, true, 'ms_val', 1, threshold2::TEXT, duration2, NOW(), NOW(), 'A', -1);

        i := i + 1;
    END LOOP;
END $$;

-- MultiStateRangeDuration monitors: 20000 done

-- ===== Phase 8: 验证数据完整性 =====

-- 监视项总数: 应等于 120000
SELECT 'Monitor count' AS metric, COUNT(*) AS value FROM ssmcitemmst WHERE monitor_no LIKE 'EXPR-%' OR monitor_no LIKE 'RDUR-%' OR monitor_no LIKE 'FEAT-%' OR monitor_no LIKE 'PACK-%' OR monitor_no LIKE 'RPAC-%' OR monitor_no LIKE 'MSRD-%';

-- 按规则类型统计
SELECT 'By rule type' AS metric, rule_flag, COUNT(*) AS count FROM ssmcitemmst WHERE monitor_no LIKE 'EXPR-%' OR monitor_no LIKE 'RDUR-%' OR monitor_no LIKE 'FEAT-%' OR monitor_no LIKE 'PACK-%' OR monitor_no LIKE 'RPAC-%' OR monitor_no LIKE 'MSRD-%' GROUP BY rule_flag ORDER BY rule_flag;

-- 数据源总数: 应约 280K-300K
SELECT 'Source count' AS metric, COUNT(*) AS value FROM ssmcsourcemst WHERE group_no LIKE 'EXPR-%' OR group_no LIKE 'RDUR-%' OR group_no LIKE 'FEAT-%' OR group_no LIKE 'PACK-%' OR group_no LIKE 'RPAC-%' OR group_no LIKE 'MSRD-%';

-- 规则表统计
SELECT 'Expression rules' AS metric, COUNT(*) AS value FROM ssmcrulecodmst WHERE related_no LIKE 'EXPR-%';
SELECT 'RangeDuration rules' AS metric, COUNT(*) AS value FROM ssmcrulerandurmst WHERE related_no LIKE 'RDUR-%';
SELECT 'RulePackageValue rules' AS metric, COUNT(*) AS value FROM ssmcrulepacvalmst WHERE related_no LIKE 'RPAC-%';
SELECT 'MultiState rules' AS metric, COUNT(*) AS value FROM ssmcrulemulstarandurmst WHERE related_no LIKE 'MSRD-%';

-- 检查唯一 tag 多样性 (验证我们是否真正使用了不同的测点)
SELECT 'Unique tags in sources' AS metric, COUNT(DISTINCT source_cod) AS value FROM ssmcsourcemst WHERE source_flag = 3 AND (group_no LIKE 'EXPR-%' OR group_no LIKE 'RDUR-%' OR group_no LIKE 'FEAT-%' OR group_no LIKE 'PACK-%' OR group_no LIKE 'RPAC-%' OR group_no LIKE 'MSRD-%');

-- 清理辅助函数
DROP FUNCTION IF EXISTS gen_tag_name;
