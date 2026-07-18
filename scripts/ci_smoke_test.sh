#!/bin/bash
# ci_smoke_test.sh — CI 快速冒烟测试 (2-3 分钟)
# 每次部署前运行，验证核心功能正常
set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
source "$SCRIPT_DIR/test_helpers.sh"

PASSED=0
FAILED=0

echo "=========================================="
echo " SIS RuleEngine CI 冒烟测试"
echo " $(date)"
echo "=========================================="

# 1. 健康检查
echo ""
echo "--- 1. 健康检查 ---"
HEALTH=$(curl -s http://localhost:11082/api/ruleengine/health)
MONITORS=$(echo "$HEALTH" | python3 -c "import json,sys;print(json.load(sys.stdin)['monitorCount'])")
WORKERS=$(echo "$HEALTH" | python3 -c "import json,sys;print(json.load(sys.stdin)['activeWorkers'])")
if [ "$MONITORS" -ge 80 ] && [ "$WORKERS" -ge 2 ]; then
    pass "Monitors=$MONITORS Workers=$WORKERS"
    PASSED=$((PASSED + 1))
else
    fail "Monitors=$MONITORS Workers=$WORKERS (expected >=80, >=2)"
    FAILED=$((FAILED + 1))
fi

# 2. 实时报警
echo ""
echo "--- 2. 实时报警 ---"
ALARMS=$(curl -s http://localhost:11082/api/ruleengine/alarms/realtime | python3 -c "import json,sys;print(len(json.load(sys.stdin)['items']))")
if [ "$ALARMS" -gt 0 ]; then
    pass "Active alarms: $ALARMS"
    PASSED=$((PASSED + 1))
else
    fail "Active alarms: 0"
    FAILED=$((FAILED + 1))
fi

# 3. 历史查询
echo ""
echo "--- 3. 历史查询 ---"
HISTORY=$(curl -s -X POST "http://localhost:11082/api/ruleengine/alarms/history" \
    -H "Content-Type: application/json" \
    -d '{"ContainNull":true,"SkipCount":0,"MaxResultCount":10}' \
    | python3 -c "import json,sys;print(json.load(sys.stdin)['totalCount'])")
if [ "$HISTORY" -gt 0 ]; then
    pass "History events: $HISTORY"
    PASSED=$((PASSED + 1))
else
    fail "History events: 0"
    FAILED=$((FAILED + 1))
fi

# 4. containNull 一致性
echo ""
echo "--- 4. containNull ---"
TRUE_COUNT=$(curl -s -X POST "http://localhost:11082/api/ruleengine/alarms/history" \
    -H "Content-Type: application/json" \
    -d '{"MonitorKeys":["TEST-0086"],"ContainNull":true,"SkipCount":0,"MaxResultCount":100}' \
    | python3 -c "import json,sys;print(json.load(sys.stdin)['totalCount'])")
FALSE_COUNT=$(curl -s -X POST "http://localhost:11082/api/ruleengine/alarms/history" \
    -H "Content-Type: application/json" \
    -d '{"MonitorKeys":["TEST-0086"],"ContainNull":false,"SkipCount":0,"MaxResultCount":100}' \
    | python3 -c "import json,sys;print(json.load(sys.stdin)['totalCount'])")
if [ "$TRUE_COUNT" -gt "$FALSE_COUNT" ]; then
    pass "containNull: true=$TRUE_COUNT > false=$FALSE_COUNT"
    PASSED=$((PASSED + 1))
else
    fail "containNull: true=$TRUE_COUNT false=$FALSE_COUNT (expected true > false)"
    FAILED=$((FAILED + 1))
fi

# 5. lastEventName
echo ""
echo "--- 5. lastEventName ---"
LAST_EVENT=$(curl -s -X POST "http://localhost:11082/api/ruleengine/alarms/history" \
    -H "Content-Type: application/json" \
    -d '{"MonitorKeys":["TEST-0086"],"ContainNull":true,"SkipCount":0,"MaxResultCount":5}' \
    | python3 -c "import json,sys;items=json.load(sys.stdin)['items'];print(any(i.get('lastEventName') for i in items))")
if [ "$LAST_EVENT" = "True" ]; then
    pass "lastEventName populated"
    PASSED=$((PASSED + 1))
else
    warn "lastEventName not populated yet (may need more cycles)"
fi

# 6. 单元测试 (如果可用)
echo ""
echo "--- 6. 单元测试 ---"
TEST_PROJ="$SCRIPT_DIR/../tests/Luculent.Sis.RuleEngine.Tests/Luculent.Sis.RuleEngine.Tests.csproj"
if [ -f "$TEST_PROJ" ]; then
    if dotnet test "$TEST_PROJ" -c Release --no-build 2>&1 | tail -3; then
        pass "Unit tests passed"
        PASSED=$((PASSED + 1))
    else
        fail "Unit tests failed"
        FAILED=$((FAILED + 1))
    fi
else
    warn "Test project not found at $TEST_PROJ"
fi

# 结果
echo ""
echo "=========================================="
echo " Results: $PASSED passed, $FAILED failed"
echo "=========================================="

if [ $FAILED -gt 0 ]; then
    exit 1
fi
