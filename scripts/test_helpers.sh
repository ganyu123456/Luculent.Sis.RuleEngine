#!/bin/bash
# SIS Rule Engine 测试工具函数
# 用法: source scripts/test_helpers.sh
set -e

RULE_ENGINE="${RULE_ENGINE:-http://localhost:11082}"
MC="${MC:-http://127.0.0.1:11080}"
JWT="${JWT:-}"

# 颜色输出
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m'

pass() { echo -e "${GREEN}[PASS]${NC} $1"; }
fail() { echo -e "${RED}[FAIL]${NC} $1"; }
warn() { echo -e "${YELLOW}[WARN]${NC} $1"; }

get_jwt() {
    curl -s -c /tmp/cookies.txt -o /dev/null "http://127.0.0.1:11080/"
    XSRF=$(grep XSRF-TOKEN /tmp/cookies.txt | awk '{print $NF}')
    JWT=$(curl -s -b /tmp/cookies.txt \
        -H "Content-Type: application/json" \
        -H "RequestVerificationToken: $XSRF" \
        -d '{"userKey":"admin","tenantKey":"Self","authenticateFrom":"Self","language":"zh-Hans","platform":null}' \
        http://127.0.0.1:11080/api/services/app/TokenAuth/Authenticate \
        | python3 -c "import json,sys; print(json.load(sys.stdin)['result']['accessToken'])" 2>/dev/null)
    if [ -z "$JWT" ]; then
        echo "ERROR: 无法获取 JWT Token" >&2
        return 1
    fi
    export JWT
    echo "JWT obtained: ${JWT:0:20}..."
}

check_health() {
    echo "=== $(date '+%H:%M:%S') 健康检查 ==="
    curl -s "$RULE_ENGINE/api/ruleengine/health" | python3 -c "
import json,sys
d=json.load(sys.stdin)
print(f'  Status: {d[\"status\"]}')
print(f'  Monitors: {d[\"monitorCount\"]}')
print(f'  Workers: {d[\"activeWorkers\"]}')
for w,c in d.get('workerDistribution',{}).items():
    print(f'    {w}: {c} monitors')
"
}

check_realmonitor() {
    echo "=== $(date '+%H:%M:%S') 实时报警 ==="
    curl -s "$RULE_ENGINE/api/ruleengine/alarms/realtime" | python3 -c "
import json,sys
d=json.load(sys.stdin)
alarms=d['items']
print(f'  Active alarms: {len(alarms)}')
if alarms:
    # show unique statusKeys
    from collections import Counter
    sk=Counter(a['statusKey'] for a in alarms)
    for k,c in sk.most_common(3):
        print(f'    {k}: {c}')
"
}

check_events_writing() {
    local monitor_key="${1:-TEST-0086}"
    echo "=== $(date '+%H:%M:%S') $monitor_key 最近事件 ==="
    curl -s -X POST "$RULE_ENGINE/api/ruleengine/alarms/history" \
        -H "Content-Type: application/json" \
        -d "{\"MonitorKeys\":[\"$monitor_key\"],\"ContainNull\":true,\"SkipCount\":0,\"MaxResultCount\":3}" \
        | python3 -c "
import json,sys
d=json.load(sys.stdin)
if not d['items']:
    print('  NO EVENTS')
else:
    for i in d['items']:
        leid=i.get('lastEventId') or 'null'
        lena=i.get('lastEventName') or 'null'
        print(f'  {i[\"occurTime\"]} statusKey={i[\"statusKey\"]} lastEventId={leid} lastEventName={lena}')
"
}

wait_for_workers() {
    local expected="${1:-2}"
    local timeout="${2:-60}"
    local elapsed=0
    while [ $elapsed -lt $timeout ]; do
        local count=$(curl -s "$RULE_ENGINE/api/ruleengine/health" 2>/dev/null | python3 -c "import json,sys;print(json.load(sys.stdin)['activeWorkers'])" 2>/dev/null || echo "0")
        if [ "$count" -ge "$expected" ]; then
            echo "  $count workers registered (${elapsed}s)"
            return 0
        fi
        sleep 2
        elapsed=$((elapsed + 2))
    done
    echo "  TIMEOUT: expected ${expected} workers after ${timeout}s"
    return 1
}

wait_for_monitors() {
    local expected="${1:-100}"
    local timeout="${2:-60}"
    local elapsed=0
    while [ $elapsed -lt $timeout ]; do
        local count=$(curl -s "$RULE_ENGINE/api/ruleengine/health" 2>/dev/null | python3 -c "import json,sys;print(json.load(sys.stdin)['monitorCount'])" 2>/dev/null || echo "0")
        if [ "$count" -ge "$expected" ]; then
            echo "  $count monitors loaded (${elapsed}s)"
            return 0
        fi
        sleep 2
        elapsed=$((elapsed + 2))
    done
    echo "  TIMEOUT: expected ${expected} monitors after ${timeout}s"
    return 1
}

check_evnetdata() {
    local monitor_key="${1:-TEST-0086}"
    if [ -z "$JWT" ]; then get_jwt; fi
    echo "=== $(date '+%H:%M:%S') EvnetData $monitor_key ==="
    for cn in true false; do
        curl -s -X POST "$MC/api/services/monitorcenter/EvnetData/GetAllForList" \
            -H "Content-Type: application/json" \
            -H "Authorization: Bearer $JWT" \
            -d "{\"Filter\":\"$monitor_key\",\"MonitorItemIds\":[],\"ContainNull\":$cn,\"SkipCount\":0,\"MaxResultCount\":5}" \
            | python3 -c "
import json,sys
r=json.load(sys.stdin)['result']
le_ok=sum(1 for i in r['items'] if i.get('lastEventId'))
le_name_ok=sum(1 for i in r['items'] if i.get('lastEventName'))
print(f'  containNull=$cn totalCount={r[\"totalCount\"]} hasLastEventId={le_ok} hasLastEventName={le_name_ok}')
"
    done
}

check_history() {
    local monitor_key="${1:-TEST-0086}"
    if [ -z "$JWT" ]; then get_jwt; fi
    echo "=== $(date '+%H:%M:%S') HistoryMonitor $monitor_key ==="
    for cn in true false; do
        curl -s -X POST "$MC/api/services/monitorcenter/HistoryMonitor/GetAllForList" \
            -H "Content-Type: application/json" \
            -H "Authorization: Bearer $JWT" \
            -d "{\"Filter\":\"$monitor_key\",\"MonitorItemIds\":[],\"ContainNull\":$cn,\"SkipCount\":0,\"MaxResultCount\":100}" \
            | python3 -c "
import json,sys
r=json.load(sys.stdin)['result']
neg=sum(1 for i in r['items'] if i.get('endTime') and i['endTime'] < i['startTime'])
print(f'  containNull=$cn totalCount={r[\"totalCount\"]} negativeDurations={neg}')
"
    done
}

check_realmonitor_modes() {
    if [ -z "$JWT" ]; then get_jwt; fi
    echo "=== $(date '+%H:%M:%S') RealMonitor ==="
    for showall in false true; do
        curl -s -X POST "$MC/api/services/monitorcenter/RealMonitor/GetAllForList" \
            -H "Content-Type: application/json" \
            -H "Authorization: Bearer $JWT" \
            -d "{\"Filter\":\"\",\"MonitorItemIds\":[],\"ShowAll\":$showall,\"SkipCount\":0,\"MaxResultCount\":200}" \
            | python3 -c "
import json,sys
r=json.load(sys.stdin)['result']
print(f'  ShowAll=$showall totalCount={r[\"totalCount\"]}')
"
    done
}

run_smoke_checks() {
    echo "=========================================="
    echo " SIS RuleEngine 冒烟检查"
    echo " $(date)"
    echo "=========================================="
    check_health
    check_realmonitor
    check_events_writing TEST-0086
    check_evnetdata TEST-0086
    check_history TEST-0086
    check_realmonitor_modes
    echo "=========================================="
    echo " 冒烟检查完成"
    echo "=========================================="
}
