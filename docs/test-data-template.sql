-- ============================================================================
-- 测试数据创建模板
-- 用于为 N 个监视项批量创建数据源和规则
-- 使用前确保 monitors 已存在于 ssmcitemmst
-- ============================================================================

-- 1. 清理旧数据源和规则（可选）
-- DELETE FROM ssmcrulerandurmst;
-- DELETE FROM ssmcsourcemst;

-- 2. 创建数据源（每个监视项 2 个）
-- Source 1: 趋势测点 (source_flag=3)
INSERT INTO ssmcsourcemst (source_no, fstusr_dtm, valid_sta, org_no, group_no, source_id, unit_nam, source_flag, source_cod)
SELECT
    'src1_' || monitor_no,
    NOW(),
    'A',
    -1,  -- 重要! 必须与 ssmcitemmst.org_no 一致
    monitor_no,  -- group_no = monitor_no 建立关联
    'tag_' || monitor_id,  -- source_id 别名
    '%',
    3,  -- Trend 类型
    'db05.test1'
FROM ssmcitemmst
WHERE enable_flag = true;

-- Source 2: 静态阈值 (source_flag=1)
INSERT INTO ssmcsourcemst (source_no, fstusr_dtm, valid_sta, org_no, group_no, source_id, unit_nam, source_flag, source_cod)
SELECT
    'src2_' || monitor_no,
    NOW(),
    'A',
    -1,
    monitor_no,
    'threshold_' || monitor_id,
    '%',
    1,  -- Static 类型
    '80'
FROM ssmcitemmst
WHERE enable_flag = true;

-- 3. 创建区间时长规则（每个监视项 1 个）
INSERT INTO ssmcrulerandurmst (ridur_no, fstusr_dtm, valid_sta, org_no, statuslin_cod, status_no, related_no, enable_flag, left_id, symbol_flag, right_id, duration_cnt)
SELECT
    'ridur_' || monitor_no,
    NOW(),
    'A',
    -1,  -- 重要! 必须与 ssmcitemmst.org_no 一致
    'satisfiled',  -- 触发状态键
    '39edc1419d86d38933a57f1e156b4991',  -- MonitorStatus ID (考核条件主题)
    monitor_no,  -- related_no = monitor_no 建立关联
    true,
    'tag_' || monitor_id,  -- left_id 匹配趋势数据源的 source_id
    1,  -- symbol_flag: 1=大于, 2=小于, 3=等于
    'threshold_' || monitor_id,  -- right_id 匹配阈值数据源的 source_id
    30  -- 持续时长(秒)
FROM ssmcitemmst
WHERE enable_flag = true;

-- 4. 验证数据完整性
SELECT 'monitors' as tbl, COUNT(*) FROM ssmcitemmst WHERE enable_flag = true
UNION ALL
SELECT 'sources', COUNT(*) FROM ssmcsourcemst
UNION ALL
SELECT 'rules', COUNT(*) FROM ssmcrulerandurmst;

-- 5. 检查每个监视项的数据源/规则数量（应全部为 2 和 1）
SELECT i.monitor_no,
       (SELECT COUNT(*) FROM ssmcsourcemst s WHERE s.group_no = i.monitor_no) as src_count,
       (SELECT COUNT(*) FROM ssmcrulerandurmst r WHERE r.related_no = i.monitor_no) as rule_count
FROM ssmcitemmst i
WHERE i.enable_flag = true
AND (
    (SELECT COUNT(*) FROM ssmcsourcemst s WHERE s.group_no = i.monitor_no) != 2
    OR (SELECT COUNT(*) FROM ssmcrulerandurmst r WHERE r.related_no = i.monitor_no) != 1
);
-- 期望: 0 rows (无异常)
