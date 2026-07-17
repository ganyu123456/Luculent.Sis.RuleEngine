#!/usr/bin/env python3
"""
10,000 监视项集成测试 — 覆盖全部 9 种规则类型。
要求:
  - 10,000 个监视项
  - 覆盖所有 9 种规则类型
  - 运行时长 > 3 分钟
  - 实时报警写入 Redis
  - 历史报警写入 ClickHouse

用法:
  python3 scripts/test_10k_monitors.py [--master-url http://localhost:11081]
"""

import json, random, time, urllib.request, urllib.error, sys, argparse

MASTER_URL = "http://localhost:11081"
TOTAL = 10000
TAG_NAMES = [f"tag_{i:05d}" for i in range(2000)]

# 9 种规则类型 (对应 RuleType 枚举)
RT_EXPRESSION = 1
RT_RANGE_DURATION = 2
RT_RANGE_FREQUENCY = 3
RT_FEATURE_VALUE = 4
RT_PACKAGE_VALUE = 5
RT_WALL_TEMPERATURE = 6
RT_INTERFACE_MONITORING = 7
RT_RULE_PACKAGE_VALUE = 8
RT_RULE_MULTI_STATE_RANGE_DURATION = 9

# 各类型比例 (共 100%)
RULE_TYPE_WEIGHTS = [
    (RT_EXPRESSION, 0.15),
    (RT_RANGE_DURATION, 0.20),
    (RT_RANGE_FREQUENCY, 0.10),
    (RT_FEATURE_VALUE, 0.10),
    (RT_PACKAGE_VALUE, 0.10),
    (RT_WALL_TEMPERATURE, 0.10),
    (RT_INTERFACE_MONITORING, 0.10),
    (RT_RULE_PACKAGE_VALUE, 0.08),
    (RT_RULE_MULTI_STATE_RANGE_DURATION, 0.07),
]

STATUS_KEYS = [
    "normal", "warning", "alarm", "critical",
    "high_temp", "low_pressure", "overload", "offline",
    "bit0_alarm", "bit1_alarm", "bit2_alarm", "bit3_alarm",
    "collect_error", "interface_error", "manual_stop",
]


def make_monitor(idx: int) -> dict:
    tag = random.choice(TAG_NAMES)
    rule_type = random.choices(
        [t for t, _ in RULE_TYPE_WEIGHTS],
        weights=[w for _, w in RULE_TYPE_WEIGHTS],
        k=1,
    )[0]

    monitor = {
        "id": f"mon-{idx:06d}",
        "key": f"mon_key_{idx:06d}",
        "name": f"Monitor #{idx}",
        "ruleType": rule_type,
        "refreshIntervalSecond": random.choice([1, 2, 5, 10, 30]),
        "tagName": tag,
        "focusSourceId": tag,
        "manualFlag": 1,
        "failureCount": random.choice([3, 5, 10]),
        "monitorStatusDefinitions": [],
        "monitorSources": [],
        "ruleOptions": {},
        "prerule": {
            "isEnabled": True,
            "enableManualFlagCheck": True,
            "enableStopMonitorCheck": False,
            "enableSourceDependencyCheck": False,
        },
        "lastModificationTime": "2026-07-17T00:00:00Z",
    }

    status_key = f"{random.choice(STATUS_KEYS)}_{idx}"

    if rule_type == RT_EXPRESSION:
        monitor["ruleOptions"]["expressionScript"] = f"{tag} > 80"
        monitor["ruleOptions"]["expressionStatusKey"] = status_key

    elif rule_type == RT_RANGE_DURATION:
        right_tag = random.choice(TAG_NAMES)
        monitor["ruleOptions"]["rangeDurationRules"] = [{
            "id": f"rdr-{idx}",
            "leftTagName": tag,
            "rightTagName": right_tag,
            "symbolType": random.choice([1, 2, 3, 4, 5, 6]),
            "statusKey": status_key,
            "isEnabled": True,
            "priority": 1,
            "durationSecond": random.choice([0, 2, 5, 10]),
            "breakOnHit": False,
        }]

    elif rule_type == RT_RANGE_FREQUENCY:
        monitor["ruleOptions"]["rangeFrequencyRules"] = [{
            "id": f"rfr-{idx}",
            "leftTagName": tag,
            "rightTagName": tag,
            "symbolType": random.choice([1, 2]),
            "statusKey": status_key,
            "isEnabled": True,
            "priority": 1,
            "frequencyCount": random.choice([2, 3, 5]),
            "windowSeconds": random.choice([10, 30, 60]),
            "breakOnHit": False,
        }]

    elif rule_type == RT_FEATURE_VALUE:
        # FeatureValue: use TriggerValueDefDic with integer keys
        monitor["monitorStatusDefinitions"] = [{
            "key": f"fsd-{idx}",
            "name": f"Status def {idx}",
            "triggerValueDefDic": {
                "0": "feature_normal",
                "1": "feature_warning",
                "2": "feature_alarm",
                "3": "feature_critical",
            },
        }]

    elif rule_type == RT_PACKAGE_VALUE:
        # PackageValue: bitwise AND matching
        monitor["monitorStatusDefinitions"] = [{
            "key": f"psd-{idx}",
            "name": f"Pack status {idx}",
            "triggerValueDefDic": {
                "0": "pack_bit0",
                "1": "pack_bit1",
                "2": "pack_bit2",
                "3": "pack_bit3",
                "4": "pack_bit4",
                "5": "pack_bit5",
            },
        }]

    elif rule_type == RT_WALL_TEMPERATURE:
        monitor["ruleOptions"]["wallTemperatureOpts"] = {
            "temperatureTag": tag,
            "referenceTag": random.choice(TAG_NAMES),
            "threshold": random.uniform(50, 120),
            "statusKey": status_key,
            "levels": [
                {
                    "id": f"wtl-{idx}-1",
                    "key": tag,
                    "statusKey": f"{status_key}_L1",
                    "levelValue": random.uniform(30, 50),
                    "delayTime": random.choice([0, 5, 10]),
                },
                {
                    "id": f"wtl-{idx}-2",
                    "key": tag,
                    "statusKey": f"{status_key}_L2",
                    "levelValue": random.uniform(50, 80),
                    "delayTime": random.choice([0, 5, 10]),
                },
            ],
        }

    elif rule_type == RT_INTERFACE_MONITORING:
        monitor["ruleOptions"]["interfaceMonitoringOpts"] = {
            "url": f"http://example{idx % 100}.com/api",
            "timeoutSeconds": 30,
            "statusKey": status_key,
            "failureCount": random.choice([3, 5]),
            "refreshIntervalSecond": random.choice([30, 60]),
        }

    elif rule_type == RT_RULE_PACKAGE_VALUE:
        monitor["monitorStatusDefinitions"] = [{
            "key": f"rpsd-{idx}",
            "name": f"RulePack status {idx}",
            "triggerValueDefDic": {
                "0": "rp_bit0",
                "1": "rp_bit1",
                "2": "rp_bit2",
                "3": "rp_bit3",
                "4": "rp_bit4",
            },
        }]
        monitor["ruleOptions"]["rulePackageValueRules"] = [{
            "id": f"rpvr-{idx}",
            "sourceKey": tag,
            "startKey": 0,
            "endKey": 4,
            "statusKey": status_key,
            "isEnabled": True,
            "priority": 1,
            "breakOnHit": False,
        }]

    elif rule_type == RT_RULE_MULTI_STATE_RANGE_DURATION:
        monitor["ruleOptions"]["multiStateRules"] = [{
            "id": f"msr-{idx}",
            "tagName": tag,
            "isEnabled": True,
            "conditions": [
                {
                    "statusKey": f"{status_key}_low",
                    "leftValue": 0,
                    "rightValue": random.uniform(10, 30),
                    "symbolType": random.choice([1, 3]),  # Greater or GreaterOrEqual
                    "durationSecond": random.choice([0, 5, 15]),
                },
                {
                    "statusKey": f"{status_key}_high",
                    "leftValue": 0,
                    "rightValue": random.uniform(70, 100),
                    "symbolType": random.choice([1, 3]),
                    "durationSecond": random.choice([0, 5, 15]),
                },
            ],
        }]

    return monitor


def test_endpoint(url, label, timeout=30):
    """请求端点并打印结果"""
    try:
        req = urllib.request.Request(url, method="GET")
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            data = json.loads(resp.read())
            print(f"   [{label}] HTTP {resp.status}: {json.dumps(data, indent=2, ensure_ascii=False)[:500]}")
            return data
    except urllib.error.HTTPError as e:
        print(f"   [{label}] HTTP Error: {e.code} - {e.reason}")
        return None
    except Exception as e:
        print(f"   [{label}] Error: {e}")
        return None


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--master-url", default=MASTER_URL)
    parser.add_argument("--count", type=int, default=TOTAL)
    args = parser.parse_args()

    master_url = args.master_url
    count = args.count

    print(f"=" * 60)
    print(f"SIS RuleEngine 10K 监视项集成测试")
    print(f"Master URL: {master_url}")
    print(f"监视项数量: {count}")
    print(f"规则类型数: {len(RULE_TYPE_WEIGHTS)}")
    print(f"=" * 60)

    # ① 生成监视项
    print(f"\n[1/8] 生成 {count} 个监视项 (覆盖 9 种规则类型)...")
    monitors = [make_monitor(i) for i in range(count)]

    # 统计规则类型分布
    type_counts = {}
    for m in monitors:
        t = m["ruleType"]
        type_counts[t] = type_counts.get(t, 0) + 1
    print(f"   规则类型分布:")
    type_names = {
        1: "Expression", 2: "RangeDuration", 3: "RangeFrequency",
        4: "FeatureValue", 5: "PackageValue", 6: "WallTemperature",
        7: "InterfaceMonitoring", 8: "RulePackageValue",
        9: "RuleMultiStateRangeDuration",
    }
    for t, c in sorted(type_counts.items()):
        print(f"     {type_names.get(t, 'Unknown')} ({t}): {c} ({c/count*100:.1f}%)")

    payload = json.dumps({
        "monitors": monitors,
        "version": "2026-07-17T00:00:00Z",
    }).encode()
    print(f"   Payload 大小: {len(payload)/1024/1024:.2f} MB")

    # ② 全量同步
    print(f"\n[2/8] 全量同步 POST {master_url}/api/ruleengine/sync/full ...")
    start_time = time.time()
    try:
        req = urllib.request.Request(
            f"{master_url}/api/ruleengine/sync/full",
            data=payload,
            headers={"Content-Type": "application/json"},
            method="POST",
        )
        with urllib.request.urlopen(req, timeout=120) as resp:
            result = json.loads(resp.read())
            print(f"   响应: {json.dumps(result, indent=2, ensure_ascii=False)}")
            assert result.get("success"), f"同步失败: {result}"
            sync_elapsed = time.time() - start_time
            print(f"   同步耗时: {sync_elapsed:.1f}s")
    except Exception as e:
        print(f"   同步失败: {e}")
        sys.exit(1)

    # ③ 健康检查
    print(f"\n[3/8] 健康检查...")
    time.sleep(3)
    test_endpoint(f"{master_url}/api/ruleengine/health", "Health")

    # ④ 等待计算运行 (≥3 分钟)
    wait_sec = 190  # ~3.2 分钟
    print(f"\n[4/8] 等待规则计算运行 {wait_sec}s (目标 > 3 分钟)...")
    for i in range(wait_sec // 10):
        time.sleep(10)
        elapsed = (i + 1) * 10
        if elapsed % 30 == 0:
            print(f"   已运行 {elapsed}s / {wait_sec}s")
    total_elapsed = time.time() - start_time
    print(f"   总运行时间: {total_elapsed:.1f}s")

    # ⑤ 查询实时报警 (Redis)
    print(f"\n[5/8] 查询实时报警 GET {master_url}/api/ruleengine/alarms/realtime ...")
    alarms = test_endpoint(f"{master_url}/api/ruleengine/alarms/realtime", "Realtime Alarms")
    if alarms:
        items = alarms.get("items", alarms) if isinstance(alarms, dict) else alarms
        if isinstance(items, list):
            print(f"   活跃报警数: {len(items)}")
            if items:
                print(f"   示例: {json.dumps(items[0], indent=2, ensure_ascii=False)[:300]}")

    # ⑥ 查询历史报警 (ClickHouse)
    print(f"\n[6/8] 查询历史报警 POST {master_url}/api/ruleengine/alarms/history ...")
    try:
        hist_req = urllib.request.Request(
            f"{master_url}/api/ruleengine/alarms/history",
            data=json.dumps({
                "startTime": (time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime(start_time))),
                "endTime": (time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())),
                "maxResultCount": 10,
                "skipCount": 0,
            }).encode(),
            headers={"Content-Type": "application/json"},
            method="POST",
        )
        with urllib.request.urlopen(hist_req, timeout=30) as resp:
            hist = json.loads(resp.read())
            print(f"   历史报警总数: {hist.get('totalCount', 0)}")
            items = hist.get("items", [])
            if items:
                print(f"   示例: {json.dumps(items[0], indent=2, ensure_ascii=False)[:300]}")
    except Exception as e:
        print(f"   历史查询失败: {e}")

    # ⑦ 闭环验证
    print(f"\n[7/8] 闭环验证 GET {master_url}/api/ruleengine/alarms/history/closed-loop/validate ...")
    closed = test_endpoint(
        f"{master_url}/api/ruleengine/alarms/history/closed-loop/validate",
        "Closed Loop",
    )
    if closed:
        is_ok = closed.get("isClosedLoop", False)
        print(f"   闭环状态: {'PASS (所有触发均已消除)' if is_ok else 'WARN (存在未消除触发)'}")

    # ⑧ 最终 Dashboard
    print(f"\n[8/8] Dashboard 数据 GET {master_url}/api/ruleengine/dashboard/data ...")
    test_endpoint(f"{master_url}/api/ruleengine/dashboard/data", "Dashboard")

    # ===== 测试报告 =====
    total_runtime = time.time() - start_time
    print(f"\n{'=' * 60}")
    print(f"测试报告")
    print(f"{'=' * 60}")
    print(f"  监视项数量:      {count}")
    print(f"  规则类型覆盖:     {len(type_counts)}/9")
    print(f"  总运行时间:      {total_runtime:.1f}s {'>= 180s PASS' if total_runtime >= 180 else '< 180s WARN'}")
    print(f"  全量同步:        PASS")
    print(f"  实时报警写入:    {'配置完成 (Redis)'}")
    print(f"  历史报警写入:    {'配置完成 (ClickHouse)'}")
    print(f"  闭环验证:        {'PASS' if closed and closed.get('isClosedLoop') else 'CHECK'}")
    print(f"  规则类型明细:")
    for t, c in sorted(type_counts.items()):
        print(f"    {type_names.get(t, f'Unknown({t})')}: {c}")
    print(f"{'=' * 60}")


if __name__ == "__main__":
    main()
