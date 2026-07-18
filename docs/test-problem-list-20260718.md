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
**根因**: 2 个 Worker 每 100ms 写一批事件，频繁的 INSERT + 可能的 MERGE 操作。
**建议**: 
- 增大 ClickHouseAlarmWriter 的 batch 间隔（当前为实时写入）
- 或合并为每秒写入一次

---

### P2 [低] Master 内存 73MB / Worker 各 ~60MB

**评估**: 对于 100 监视项规模，内存使用正常。1000+ 监视项时需关注 Worker 内存增长（主要来自 CalculationState 字典）。

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

## 四、已完成的架构清理 (2026-07-18)

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
| P3 | P1 - ClickHouse CPU | 性能优化 |
