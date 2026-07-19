# 排查手册

## 一、ClickHouse 数据查询

### 直连 ClickHouse 查询

```bash
# 查询特定监视项事件
docker exec ruleengine-clickhouse clickhouse-client -d ruleengine --query "
SELECT monitor_id, monitor_key, status_key, status_name, occur_time,
       trigger_value, max_value, min_value,
       last_event_id, last_event_name
FROM alarm_events
WHERE monitor_id = 'xxx' OR monitor_key = 'xxx'
ORDER BY occur_time DESC
LIMIT 20
FORMAT TSVWithNames
"

# 查事件总数
docker exec ruleengine-clickhouse clickhouse-client -d ruleengine --query "
SELECT count() FROM alarm_events
WHERE occur_time >= now() - INTERVAL 1 HOUR
"

# 查活跃告警（最后事件 status_key != ''）
docker exec ruleengine-clickhouse clickhouse-client -d ruleengine --query "
SELECT monitor_id, monitor_key, status_key, occur_time, trigger_value
FROM alarm_events
WHERE occur_time >= now() - INTERVAL 10 MINUTE
ORDER BY occur_time DESC
LIMIT 1 BY monitor_id
FORMAT TSVWithNames
" | head -20

# 检测事件交替异常（相邻同状态）
docker exec ruleengine-clickhouse clickhouse-client -d ruleengine --query "
SELECT count() FROM (
    SELECT monitor_id, status_key,
           lagInFrame(status_key) OVER (PARTITION BY monitor_id ORDER BY occur_time) AS prev,
           row_number() OVER (PARTITION BY monitor_id ORDER BY occur_time) AS rn
    FROM alarm_events
    WHERE occur_time >= now() - INTERVAL 1 HOUR
) WHERE rn > 1 AND status_key = prev
"
```

### 特定监视项深度排查

```bash
MONITOR_ID="expr-mon-0010916"

# 1. 查 Postgres 配置
docker exec sis-postgres psql -U postgres -d sis1 -c "
SELECT i.monitor_no, i.monitor_id, i.monitor_nam, i.rule_flag, i.prerule_no, i.enable_flag,
       r.symbol_flag, r.left_id, r.right_id, r.duration_cnt, r.statuslin_cod
FROM ssmcitemmst i
JOIN ssmcrulerandurmst r ON r.related_no = i.monitor_no
WHERE i.monitor_id = '$MONITOR_ID'
"

# 2. 查 ClickHouse 事件
docker exec ruleengine-clickhouse clickhouse-client -d ruleengine --query "
SELECT monitor_id, status_key, status_name, occur_time, trigger_value,
       max_value, min_value, last_event_id, last_event_name, unit
FROM alarm_events WHERE monitor_id = '$MONITOR_ID'
ORDER BY occur_time DESC LIMIT 20 FORMAT TSVWithNames
"

# 3. 查 Redis 实时状态
docker exec ruleengine-redis redis-cli HGETALL "active_alarm:$MONITOR_ID"
```

## 二、MonitorCenter API 测试

### 获取 JWT Token

```bash
curl -s -c /tmp/cookies.txt http://127.0.0.1:11080/
XSRF=$(grep XSRF-TOKEN /tmp/cookies.txt | awk '{print $NF}')
JWT=$(curl -s -b /tmp/cookies.txt \
    -H "Content-Type: application/json" \
    -H "RequestVerificationToken: $XSRF" \
    -d '{"userKey":"admin","tenantKey":"Self","authenticateFrom":"Self","language":"zh-Hans","platform":null}' \
    http://127.0.0.1:11080/api/services/app/TokenAuth/Authenticate \
    | python3 -c "import json,sys; d=json.load(sys.stdin); print(d['result']['accessToken'])")
echo "$JWT" > /tmp/jwt_token.txt
```

### 测试三个核心 API

```bash
JWT=$(cat /tmp/jwt_token.txt)

# 1. EvnetData/GetAllForList — 事件列表
curl -s -H "Authorization: Bearer $JWT" -H "Content-Type: application/json" \
  -X POST "http://127.0.0.1:11080/api/services/monitorcenter/EvnetData/GetAllForList" \
  -d '{"containNull":false,"skipCount":0,"maxResultCount":5}' | python3 -m json.tool

# 2. HistoryMonitor/GetAllForList — 历史列表
NOW_MS=$(date +%s)000
HOUR_AGO_MS=$((NOW_MS - 3600000))
curl -s -H "Authorization: Bearer $JWT" -H "Content-Type: application/json" \
  -X POST "http://127.0.0.1:11080/api/services/monitorcenter/HistoryMonitor/GetAllForList" \
  -d "{\"skipCount\":0,\"maxResultCount\":5,\"startTime\":$HOUR_AGO_MS,\"endTime\":$NOW_MS}" \
  | python3 -m json.tool

# 3. RealMonitor/GetAllForList — 实时列表
curl -s -H "Authorization: Bearer $JWT" -H "Content-Type: application/json" \
  -X POST "http://127.0.0.1:11080/api/services/monitorcenter/RealMonitor/GetAllForList" \
  -d '{"showAll":false,"skipCount":0,"maxResultCount":5}' | python3 -m json.tool
```

### API 验收脚本 (Python)

```bash
JWT=$(cat /tmp/jwt_token.txt)
curl -s -H "Authorization: Bearer $JWT" -H "Content-Type: application/json" \
  -X POST "http://127.0.0.1:11080/api/services/monitorcenter/EvnetData/GetAllForList" \
  -d '{"containNull":false,"skipCount":0,"maxResultCount":100}' | python3 -c "
import json, sys
d = json.load(sys.stdin)['result']
items = d['items']
print(f'totalCount: {d[\"totalCount\"]}, items: {len(items)}')
# 字段完整性检查
empty_last_event = sum(1 for i in items if not i.get('lastEventName'))
empty_status_name = sum(1 for i in items if not i.get('occurMonitorStatusName'))
empty_value = sum(1 for i in items if not i.get('occurValue'))
print(f'lastEventName 为空: {empty_last_event}')
print(f'occurMonitorStatusName 为空: {empty_status_name}')
print(f'occurValue 为空: {empty_value}')
# 事件交替检查
by_key = {}
for i in items: by_key.setdefault(i['key'], []).append(i)
dup = sum(1 for k,evts in by_key.items()
    for j in range(1,len(evts)) if evts[j]['occurMonitorStatusKey']==evts[j-1]['occurMonitorStatusKey'])
print(f'相邻事件 statusKey 相同 (应为0): {dup}')
"
```

### 直接测试 Rule Engine API（无需认证）

```bash
# 历史查询
curl -X POST http://localhost:11082/api/ruleengine/alarms/history \
  -H "Content-Type: application/json" \
  -d '{"maxResultCount":5,"skipCount":0,"containNull":false}'

# 实时查询
curl http://localhost:11082/api/ruleengine/alarms/realtime

# 闭环验证
curl http://localhost:11082/api/ruleengine/alarms/history/closed-loop/validate
```

## 三、数据问题排查流程

### 排查 "maxValue/minValue = 0" 问题

```
1. ClickHouse 确认: 查 alarm_events WHERE monitor_id = 'xxx'
   → 看 max_value/min_value 列是否全为 0

2. 分类判断:
   - Expression 监视项: 检查 CalculateRuleExpression.TriggerValue 是否已设置
   - RangeDuration 监视项: trigger 事件 max/min=0 是正常行为（上一段为 clear），
     clear 事件的 max/min 才是告警期间的极值

3. 根因验证:
   - CalculateRuleExpression.cs line 89: RuleCalculateResult.TriggerValue 是否赋值?
   - CalculationState.cs line 48-49: [JsonIgnore(WhenWritingDefault)] 导致 0 值不持久化
```

### 排查 "status_name 为空" 问题

```
1. ClickHouse 确认: status_name 列在 alarm_events 中是否始终为 NULL
   → 如果是，则 Worker 写入侧未设置 StatusName

2. 定位写入点: WorkerCalculationService.cs AlarmEvent 创建处
   → 检查 StatusName 是否被赋值

3. MonitorCenter 侧: 确认分布式模式下 statusKey → 显示名翻译是否生效
   → MonitorStatusDefinition 表查询
```

### 排查 "API 返回 0 条数据" 问题

```
1. 确认分布式模式已开启:
   docker inspect sis-service --format '{{range .Config.Env}}{{println .}}{{end}}' | grep MonitorCenter

2. 直连 Rule Engine API 测试:
   curl -X POST http://localhost:11082/api/ruleengine/alarms/history -d '{"maxResultCount":5}'
   → 有数据 = MonitorCenter 侧问题; 无数据 = Rule Engine 侧问题

3. Rule Engine 侧:
   - 检查 Master 日志: docker logs ruleengine-master --tail 50
   - 检查 ClickHouse 连接: docker exec ruleengine-clickhouse clickhouse-client -q "SELECT 1"
   - 检查 monitorKeys IN 子句是否超过 5000 项限制

4. MonitorCenter 侧:
   - 检查 sis-service 日志
   - 确认 MonitorCenter DLL 是最新版本
   - 检查 monitorItemIds 过滤与 PostgreSQL 中的 monitor_id 是否匹配
```

## 四、常用运维命令

```bash
# 查看 Master 日志
docker logs ruleengine-master --tail 50 -f

# 查看 Worker 日志
docker logs 03-luculentsisruleengine-worker-1 --tail 50

# 查看 sis-service 日志
docker logs sis-service --tail 50

# 重启所有 Rule Engine 服务
cd 03-Luculent.Sis.RuleEngine && docker compose restart

# 清空 ClickHouse 数据
docker exec ruleengine-clickhouse clickhouse-client -d ruleengine -q "TRUNCATE TABLE alarm_events"

# 清空 Redis
docker exec ruleengine-redis redis-cli FLUSHALL

# 验证 PostgreSQL 监视项数量
docker exec sis-postgres psql -U postgres -d sis1 -c \
  "SELECT COUNT(*) FROM ssmcitemmst WHERE enable_flag = true"
```
