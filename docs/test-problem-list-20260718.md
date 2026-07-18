# Rule Engine 测试问题清单

> 日期: 2026-07-18
> 测试类型: 功能测试 + 性能测试 + 异常场景测试
> 测试环境: Docker Compose (2 Worker + 1 Master + ClickHouse + Redis)
> 测试数据: 100 监视项 + 2 前置规则

---

## 架构说明

Rule Engine 采用**纯事件流模型**：
- 每条记录 = 一次状态变更（触发或消除），全部 INSERT，无 UPDATE
- `StatusKey` 区分事件类型：非空 = 触发，空字符串 = 消除
- 事件配对/持续时长计算/StatusKey→StatusName 映射均由 MonitorCenter 侧完成
- Rule Engine 只负责：规则计算 → 写事件 → 提供原始事件流查询

**计算架构（模式 A — 采集与计算分离）：**
```
DataAcquisitionService (PeriodicTimer 1s)
  → TrendDB.ReadBatchAsync(allTags)
  → TagValueStore.Update(values)
        ↓
WorkerCalculationService (PeriodicTimer 1s, SemaphoreSlim 防重叠)
  → TagValueStore.Values (读缓存，不查 TrendDB)
  → 过滤到期监视项 → Parallel.ForEachAsync
  → 前置规则检查 → 规则计算 → 写事件
```
- 采集和计算解耦，TrendDB 每秒只查 1 次（之前每 100ms 查 1 次，每秒 5-6 次）
- 计算超时自动跳过下一周期，不排队堆积

---

## 一、功能缺陷

### F1 [严重] RangeDuration 前置规则始终返回 false，导致 50/100 监视项被永久抑制

**文件**: `Worker/Calculation/PreruleEvaluationService.cs:130-167`

**根因**: `EvaluateRangeDuration` line 157: `if (rule.BreakOnHit) return true;`
当 `BreakOnHit` 为 false（默认值）时，即使 duration 条件已满足，方法也永远不会返回 true，最终 fall through 到 line 166 `return false`。

**影响**: 
- 前置规则 `prerule-rangedur-00001` 关联的 50 个监视项 (test0001-test0050) 全部被抑制
- 只有前置规则 2 (Expression) 关联的 50 个监视项 (test0051-test0100) 正常触发

**修复建议**:
```csharp
// 修改为: 条件满足 duration 后设置 hit 标志，BreakOnHit 控制是否立即返回
bool anyHit = false;
foreach (var rule in def.RuleRangeDurations.OrderBy(r => r.Priority))
{
    // ... 条件判断 ...
    if (elapsed >= rule.DurationSecond)
    {
        anyHit = true;
        if (rule.BreakOnHit) return true;
    }
}
return anyHit;
```

---

### F2 [高] 前置规则评估无日志输出，运行状态不可观测

**文件**: `Worker/Calculation/PreruleEvaluationService.cs:35-53`

**根因**: `EvaluateAllAsync` 只使用 `_logger.LogDebug`（当前日志级别不输出）和 `_logger.LogWarning`（仅异常时）。

**影响**: 无法判断后台评估循环是否在运行、当前规则状态是什么。

**修复建议**: 添加 `_logger.LogInformation` 日志：
- 每次评估循环开始时: "前置规则评估: {Count} 条"
- 每条规则评估结果: "前置规则 {Id} = {State}"

---

### F3 [低] FetchSourceDataAsync 使用 Key 而非 SourceKey 获取固定值数据源

**文件**: `Worker/Calculation/PreruleEvaluationService.cs:79-80`

**根因**: 
```csharp
var keys = sources.Select(s => string.IsNullOrEmpty(s.Key) ? s.SourceKey : s.Key)
```
当 `Key` 非空时始终使用 Key（别名），不考虑 SourceType。对于常量值数据源（source_cod="80"），Key="threshold_src" 会从 TrendDB 读取随机值而非固定值 80。

**影响**: 前置规则的右值比较使用随机数据而非预设阈值，导致 RangeDuration 前置规则的条件判断不可预期。

**修复建议**: 根据 SourceType 区分：类型为常量的 source 直接 parse SourceKey 为数值，不从 TrendDB 读取。

---

### F4 [低] Worker 重启时活跃报警数波动

**观察**: Worker-2 停止后，活跃报警从 50 → 27；重启后逐渐恢复。
**根因**: Worker 重启后状态缓存丢失，原有触发条件需重新累积 duration 才能再次触发。
**影响**: 短暂的报警丢失（1-2 个计算周期）。

---

### F5 [低] 闭环验证检测出开环事件

**观察**: 75 次触发 vs 25 次消除，`isClosedLoop: false`。
**根因**: 闭环验证 `HistoryAlarmService.ValidateClosedLoopAsync` 通过 `monitorLastStatus` 判断：最后一条 `StatusKey` 非空即视为开环。生产环境中消除事件会逐步增多。
**影响**: 功能正常，仅验证结果显示有开环事件。

---

## 二、性能问题

### P1 [中] ClickHouse CPU 持续 86%

**观察**: `docker stats` 显示 `ruleengine-clickhouse` CPU 86.26%。
**分析**:
- Worker CPU 侧：已通过模式 A 解决（采集与计算分离、1s 周期替代 100ms 空转，预计 Worker CPU 降 70-80%）
- ClickHouse CPU 侧：写入频率未变（批量 INSERT 依然由事件驱动），CPU 高主要是 ClickHouse 自身的 merge 开销
**建议**: 
- 调整 ClickHouse merge 参数（`max_bytes_to_merge_at_max_space_in_pool` 等）
- `ClickHouseAlarmWriter` 已优化为批量 5000 + 1s 定时刷新 + 服务端 async_insert (详见 4.3)

---

### P2 [低] Master 内存 73MB / Worker 各 ~60MB

**评估**: 对于 100 监视项规模，内存使用正常。

**扩展分析**:
| 规模 | Worker 内存 (4 Worker) | Master 内存 |
|------|------------------------|-------------|
| 100 | ~60 MB | ~73 MB |
| 50,000 | ~170-200 MB | ~150 MB |
| 1,000,000 | ~250-350 MB | ~200 MB |

- 单监视项 CalculationState 开销 ~2 KB (Dictionary entry + string keys + bool/datetime fields)
- 1M 监视项 × 2KB ≈ 2GB / 4 Worker = ~500MB per Worker (最坏情况)
- 实际活跃率 ~10%，大部分监视项无活跃状态，实际约 50-60MB + 活跃开销
- 现代服务器 8-16GB 内存完全够用，无需担心

---

### P3 [信息] API 响应时间

| 端点 | 延迟 |
|------|------|
| `/api/ruleengine/health` | 3ms |
| `/api/ruleengine/alarms/realtime` | 9ms |
| `/api/ruleengine/alarms/history` | 74ms |
| `/api/ruleengine/dashboard/data` | 10ms |

所有 API 响应时间良好，历史查询因涉及 ClickHouse 查询略慢，在可接受范围。

---

## 三、异常场景测试结果

### E1 [通过] Worker 断连/重连
- Worker-2 停止后，Master 在 5s 内检测到，自动重分区（100 监视项 → Worker-1）
- Worker-2 重启后，10s 内重新注册，恢复 50/50 分布
- 死 Worker 清理后台循环正常工作

### E2 [通过] 配置重分区
- Worker 变更后自动触发 Cost-Aware 重分区
- 分区结果均衡 (50/50)

### E3 [通过] 空前置规则加载被拒绝
- Admin API `/load-prerules` 正确拒绝空列表

### E4 [通过] 检索条件过滤
- `monitorKeys: ["test00510261"]` 正确过滤 (2 条结果)
- `containNull=true/false` 过滤正确 (3241 vs 3074 totalCount)
- `StatusKey` 过滤正确区分触发/消除事件

### E5 [通过] 事件流写入验证
- ClickHouse 有持续新增事件 (INSERT only, 无 UPDATE)
- StatusKey="" 消除事件正常产生 (92+)
- 事件配对由 MonitorCenter 侧 `HistoryMonitorAppService.GetAllForListFromRuleEngineAsync` 完成

---

## 四、已完成的架构清理与优化 (2026-07-18)

### 4.1 旧方案残留清理

以下旧方案残留已全部清理，Rule Engine 为纯事件流模型：

| 清理项 | 说明 |
|--------|------|
| `EventType` 枚举 | 已删除 `Shared/Enums/EventType.cs`。StatusKey 已能区分触发/消除 |
| `event_type` 列 | 从 INSERT、AlarmEvent、AlarmEventDTO 全部移除 |
| `clear_time` 列 | 从 INSERT、AlarmEvent 全部移除。事件流无 UPDATE 操作 |
| `ClearTime` DTO 字段 | 从 `AlarmEventDTO` 移除 |
| `clearTime` MonitorCenter 字段 | 从 `RuleEngineClient.HistoryAlarmItem` 移除 |
| 事件配对逻辑 | 从 `HistoryAlarmService` (Rule Engine) 移至 `HistoryMonitorAppService` (MonitorCenter) |
| `EventTypes` 过滤参数 | 从 API、DTO、RuleEngineClient 全部移除 |
| `AlarmSnapshot.ToAlarmEvent()` | 已删除（死代码） |
| `CalculationState.PreviousEventOccurTime` | 已删除 |

### 4.2 计算架构优化（模式 A）

| 项目 | 优化前 | 优化后 |
|------|--------|--------|
| TrendDB 查询频率 | 每 100ms（每秒 5-6 次，含空转） | 每 1s（固定周期，不空转） |
| 数据流 | 计算循环内直接查 TrendDB | DataAcquisitionService → TagValueStore → 计算读缓存 |
| 重叠保护 | 无（可能堆积） | SemaphoreSlim(1,1)，超时跳过 |
| 资源预估 | Worker CPU 空转 80% 循环 | Worker CPU 降 70-80% |

新增文件：
- `Worker/DataAcquisition/TagValueStore.cs` — 线程安全实时值缓存
- `Worker/DataAcquisition/DataAcquisitionService.cs` — 1s 周期数据采集 BackgroundService

修改文件：
- `Worker/WorkerCalculationService.cs` — 移除 TrendDB 查询依赖，改为读 TagValueStore 缓存；实时/历史报警写入增加状态去重
- `Worker/Program.cs` — 注册 TagValueStore + DataAcquisitionService
- `Worker/Storage/ClickHouseAlarmWriter.cs` — 批量 500→5000, 1s 定时刷新, 连接复用, async_insert, Channel 容量 20000
- `Worker/Storage/RedisAlarmWriter.cs` — `GetLastEventStatusesAsync` 改为 SMEMBERS + Pipeline HGET 批量恢复
- `Worker/Storage/ProductionAlarmWriter.cs` — 三级降级恢复 (Redis → ClickHouse → 空状态)

### 4.3 ClickHouse 写入三重优化

参照 ClickHouse 官方最佳实践，对 `ClickHouseAlarmWriter` 做了三重保障：

| 优化项 | 优化前 | 优化后 | 依据 |
|--------|--------|--------|------|
| 批量大小 | 500 条/批 | 5000 条/批 | 官方建议 ≥1000，理想 10000+ |
| 定时刷新 | 无（仅靠满批触发） | 最多 1s 刷新一次 | 保证数据时效性 ≤1s |
| 连接复用 | 每次写入新建连接 | FlushLoop 复用单连接 | 减少 TCP 握手开销 |
| 服务端 async_insert | 无 | SET async_insert=1 | 小 INSERT 由 ClickHouse 合并为大 part 后再写盘 |
| Channel 容量 | 10000 | 20000 | 缓冲区更大，减少丢弃 |

**双重保障机制**:
- **应用层**: 5000 条满批 OR 1s 定时到 → 触发 INSERT
- **服务端**: ClickHouse `min_rows_for_async_insert=1000, async_insert_busy_timeout_ms=1000` → 自动合并小 INSERT 为大 part
- **效果**: 写入频率从之前的每秒多次降为最多 1 次/秒，大幅降低 ClickHouse merge 压力

### 4.4 状态恢复重新设计 (面向 100 万监视项)

原有方案通过 ClickHouse `WHERE monitor_id IN (...)` 批量查询，IN 子句可膨胀到 250K+ ID，对 ClickHouse 造成毁灭性查询压力。

**新方案**: 三级降级恢复

| 层级 | 数据源 | 查询方式 | 适用场景 |
|------|--------|----------|----------|
| 主路径 | Redis | SMEMBERS (获取活跃集合) + Pipeline HGET (批量查 status_key) | 正常运行时 |
| 回退 | ClickHouse | 分批 IN (≤500/批)，`LIMIT 1 BY monitor_id` | Redis 不可用时 |
| 降级 | 空状态 | 全部返回 "" (正常态) | ClickHouse 也不可用时 |

**Redis 方案分析**:
- `SMEMBERS` 返回活跃报警 ID 集合 → 100 万监视项 × 10% 活跃 = 10 万个活跃 ID
- 10 万活跃 ID 中，请求的 ID 才需要 Pipeline HGET → 实际批量 ≤1000/批
- 非活跃监视项 → 直接返回 "" (正常态)，无需网络查询
- `SMEMBERS` O(N) 遍历 10 万元素 ~50-100ms，可接受
- Pipeline HGET 每批 1000 个，100 批 × 0.5ms = 50ms

**`ProductionAlarmWriter` 三级降级实现**:
```csharp
// 主路径: Redis
try { return await _realtime.GetLastEventStatusesAsync(monitorIds); }
catch { /* fallback */ }
// 回退: ClickHouse
try { return await _history.GetLastEventStatusesAsync(monitorIds); }
catch { return monitorIds.ToDictionary(id => id, _ => (string?)""); }
```

---

### 4.5 实时/历史报警写入去重

**问题**: `ProcessMonitorAsync` 每个计算周期无条件写入 Redis 实时报警和 ClickHouse 历史事件，即使状态未变化。稳态下大量冗余写入。

**修复**: 三条写路径全部增加状态去重，只在状态变化时写入：

| 路径 | 去重条件 | 效果 |
|------|----------|------|
| 正常触发 | `newStatus != state.PreviousStatus` | 持续报警不重复写 |
| 正常消除 | `newStatus != state.PreviousStatus` | 持续正常不重复删 |
| 前置规则 suppress 清除 | `PreviousStatus != ""` | 已清除后不重复删 |

**去重收益**:

| 稳态场景 | 优化前 (30s 刷新间隔) | 优化后 |
|----------|----------------------|--------|
| 持续报警 Redis | 每 30s 写 (HSET+SADD+EXPIRE) | 不写 |
| 持续正常 Redis | 每 30s 删 (DEL+SREM) | 不写 |
| 持续报警 ClickHouse | 每 30s 写 (INSERT) | 不写 |

**关键意义**: 实时报警 `OccurTime` 保留真正的报警开始时间，不再被后续周期覆盖。

---

## 五、单元测试状态

- 现有测试: 53 个全部通过 (WorkerCalculationService_Tests: 7 个)
- 缺失测试:
  - PreruleEvaluationService 测试（EvaluateExpression, EvaluateRangeDuration, BreakOnHit 逻辑）
  - ClickHouseAlarmWriter 测试（FormatRow, WriteBatchAsync）
  - end-to-end 集成测试

---

## 六、修复优先级

| 优先级 | 缺陷 | 原因 |
|--------|------|------|
| P0 | F1 - RangeDuration BreakOnHit | 阻塞 50% 监视项，数据面 |
| P1 | F2 - 前置规则评估日志 | 无法调试，阻塞后续问题排查 |
| P2 | F3 - Key vs SourceKey | 前置规则数据正确性 |
| P3 | F4 - Worker 重启报警波动 | 影响可控 |
| P3 | F5 - 闭环验证开环事件 | 功能正常，仅验证提示 |
| P3 | P1 - ClickHouse CPU | Worker 侧已通过模式 A 优化，ClickHouse 写入侧已通过三重优化 (4.3) 完成，ClickHouse merge 侧需单独调整参数 |
