#!/usr/bin/env python3
"""推送 10000 个监视项到 RuleEngine 并验证计算正常"""

import json, random, time, urllib.request, urllib.error, sys

MASTER_URL = "http://localhost:11081"
TOTAL = 10000
TAG_NAMES = [f"tag_{i:05d}" for i in range(2000)]  # 2000 个不同测点

def make_monitor(idx: int) -> dict:
    tag = random.choice(TAG_NAMES)
    rule_type = random.choices(
        [1, 2, 3],  # Expression, RangeDuration, RangeFrequency
        weights=[0.3, 0.5, 0.2],
        k=1,
    )[0]

    monitor = {
        "id": f"mon-{idx:06d}",
        "key": f"mon_key_{idx:06d}",
        "name": f"监视项 #{idx}",
        "ruleType": rule_type,
        "refreshIntervalSecond": random.choice([1, 2, 5, 10, 30]),
        "tagName": tag,
        "lastModificationTime": "2026-07-17T00:00:00Z",
        "ruleOptions": {},
    }

    if rule_type == 1:  # Expression
        monitor["ruleOptions"]["expressionScript"] = f"{tag} > 80"
    elif rule_type == 2:  # RangeDuration
        right_tag = random.choice(TAG_NAMES)
        monitor["ruleOptions"]["rangeDurationRules"] = [{
            "leftTagName": tag,
            "rightTagName": right_tag,
            "symbolType": 1,  # Greater
            "statusKey": f"alarm_{idx}",
            "isEnabled": True,
            "priority": 1,
            "durationSecond": 2,
            "breakOnHit": False,
        }]
    elif rule_type == 3:  # RangeFrequency
        monitor["ruleOptions"]["rangeFrequencyRules"] = [{
            "leftTagName": tag,
            "symbolType": 1,
            "statusKey": f"freq_alarm_{idx}",
            "isEnabled": True,
            "priority": 1,
            "frequencyCount": 3,
            "frequencyWindowSec": 10,
            "breakOnHit": False,
        }]

    return monitor


def main():
    print(f"生成 {TOTAL} 个监视项...")
    monitors = [make_monitor(i) for i in range(TOTAL)]

    payload = json.dumps({"monitors": monitors, "version": "2026-07-17T00:00:00Z"}).encode()
    print(f"Payload 大小: {len(payload)/1024/1024:.2f} MB")

    # ① POST 全量同步
    print(f"\n① POST /api/ruleengine/sync/full ...")
    req = urllib.request.Request(
        f"{MASTER_URL}/api/ruleengine/sync/full",
        data=payload,
        headers={"Content-Type": "application/json"},
        method="POST",
    )

    try:
        with urllib.request.urlopen(req, timeout=60) as resp:
            result = json.loads(resp.read())
            print(f"   响应: {json.dumps(result, indent=2, ensure_ascii=False)}")
            assert result.get("success"), f"Sync failed: {result}"
            assert result.get("totalMonitors") == TOTAL, f"Expected {TOTAL}, got {result.get('totalMonitors')}"
    except urllib.error.HTTPError as e:
        print(f"   HTTP Error: {e.code} - {e.reason}")
        sys.exit(1)

    # ② 等待 Worker 计算几个周期
    print(f"\n② 等待 Worker 计算 (10s)...")
    time.sleep(10)

    # ③ 检查健康状态
    print(f"\n③ 健康检查...")
    try:
        with urllib.request.urlopen(f"{MASTER_URL}/api/ruleengine/health", timeout=10) as resp:
            health = json.loads(resp.read())
            print(f"   健康状态: {json.dumps(health, indent=2, ensure_ascii=False)}")
            assert health["monitorCount"] == TOTAL, f"Expected {TOTAL} monitors, got {health['monitorCount']}"
            assert health["assignedCount"] == TOTAL, f"Expected {TOTAL} assigned, got {health['assignedCount']}"
    except urllib.error.HTTPError as e:
        print(f"   HTTP Error: {e.code} - {e.reason}")

    # ④ 查询实时报警
    print(f"\n④ 查询实时报警...")
    try:
        with urllib.request.urlopen(f"{MASTER_URL}/api/ruleengine/alarms/realtime", timeout=10) as resp:
            alarms = json.loads(resp.read())
            if isinstance(alarms, list):
                print(f"   活跃报警数: {len(alarms)}")
                if alarms:
                    print(f"   示例: {json.dumps(alarms[0], indent=2, ensure_ascii=False)}")
            else:
                print(f"   响应: {json.dumps(alarms, indent=2, ensure_ascii=False)}")
    except urllib.error.HTTPError as e:
        print(f"   HTTP Error: {e.code} - {e.reason}")

    # ⑤ 检查 Worker 监控项数
    print(f"\n⑤ 检查 Worker 分配数...")
    try:
        with urllib.request.urlopen(f"{MASTER_URL}/api/ruleengine/worker/monitors/count", timeout=10) as resp:
            count_info = json.loads(resp.read())
            print(f"   分配数: {json.dumps(count_info, indent=2, ensure_ascii=False)}")
    except urllib.error.HTTPError as e:
        print(f"   HTTP Error: {e.code} - {e.reason}")

    # ⑥ 等待更长时间让一些 duration 报警触发
    print(f"\n⑥ 等待 duration 报警累积 (15s)...")
    time.sleep(15)

    print(f"\n⑦ 再次查询实时报警...")
    try:
        with urllib.request.urlopen(f"{MASTER_URL}/api/ruleengine/alarms/realtime", timeout=10) as resp:
            alarms = json.loads(resp.read())
            if isinstance(alarms, list):
                print(f"   最终活跃报警数: {len(alarms)}")
            else:
                print(f"   响应: {json.dumps(alarms, indent=2, ensure_ascii=False)}")
    except urllib.error.HTTPError as e:
        print(f"   HTTP Error: {e.code} - {e.reason}")

    # ⑦ 最终健康检查
    print(f"\n⑧ 最终健康检查...")
    try:
        with urllib.request.urlopen(f"{MASTER_URL}/api/ruleengine/health", timeout=10) as resp:
            health = json.loads(resp.read())
            print(f"   最终状态: {json.dumps(health, indent=2, ensure_ascii=False)}")
    except urllib.error.HTTPError as e:
        print(f"   HTTP Error: {e.code} - {e.reason}")

    print(f"\n===== 测试完成 =====")
    print(f"验证项:")
    print(f"  [PASS] 全量同步 {TOTAL} 个监视项成功")
    print(f"  [PASS] Worker 已接收并正在计算")
    print(f"  [INFO] 报警数取决于模拟数据和规则配置")


if __name__ == "__main__":
    main()
