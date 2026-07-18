# Rule Engine 测试问题清单

> 日期: 2026-07-19 (Session 4)
> 测试类型: 单元测试 + 性能测试 + 端到端集成测试
> 测试环境: Docker Compose (2 Worker + 1 Master + ClickHouse + Redis + PostgreSQL)
> 测试数据: 10,000 监视项 + 2 前置规则 (各关联 5,000 监视项, 统一使用 db05.test1)

---

## 测试结果总览

| 类别 | 结果 |
|------|------|
| 单元测试 | **77/77 通过** (0 失败, 0 跳过) |
| 性能测试 | **4/4 通过** |
| E2E 集成 | 10,000/10,000 监视项产生事件, 5,001 活跃报警 |
| 异常场景 | 待执行 |

### E2E 集成统计 (10,000 监视项, 10分钟)

| 指标 | 值 |
|------|-----|
| ClickHouse 总事件 (10min) | 52,260 |
| 触发事件 (StatusKey=satisfiled) | 25,294 |
| 消除事件 (StatusKey="") | 26,800 |
| Redis 活跃报警 | 5,001 |
| 产生事件的监视项 | 10,000/10,000 (100%) |
| Worker 分布 | 2 × 5,000 监视项 |
| 事件速率 | ~87 events/s |

### 系统资源 (10K 监视项稳态)

| 组件 | CPU | 内存 |
|------|-----|------|
| Worker-1 | 29.8% | 101.5 MB |
| Worker-2 | 41.5% | 121.7 MB |
| Master | 28.8% | 286.3 MB |

---

## 一、本 Session 修复的问题

### F7 [已修复] MonitorCenter 分页参数不生效导致 MonitorCenterClient 无限循环

**根因**: ABP 动态 Web API 的 `GetAllMonitors(int skip, int take)` 参数绑定失败，`skip`/`take` 查询参数被忽略，API 始终返回全量 10,000 条。`MonitorCenterClient` 分页循环在 `batch.Count (10000) >= BatchSize (5000)` 条件下永不退出。

**修复**: `MonitorCenterClient.cs:17` — `BatchSize` 从 5000 提高到 20000，使单次请求返回所有数据后满足 `batch.Count < BatchSize` 条件退出循环。同时添加 `MaxIterations = 100` 安全守卫防止无限循环。

**变更文件**:
- `Master/Services/MonitorCenterClient.cs` — BatchSize 20000 + MaxIterations 100

### F8 [已修复] gRPC 消息体超过默认 4MB 限制

**根因**: 10,000 监视项的配置数据超过 gRPC 默认 `MaxReceiveMessageSize` (4MB)，导致 Worker 端报 `ResourceExhausted: Received message exceeds the maximum configured message size`，Worker 反复断连重连。

**修复**: Master 和 Worker 的 gRPC 消息大小限制提高到 50MB。
- `Master/Program.cs` — `AddGrpc()` 添加 `MaxReceiveMessageSize`/`MaxSendMessageSize` = 50MB
- `Worker/Services/GrpcConnectionService.cs` — `GrpcChannel.ForAddress()` 添加 `GrpcChannelOptions` 同样配置

---

## 二、遗留问题

### G5 [信息] MonitorCenter GetAllMonitors 分页参数绑定失败

**现象**: `GetAllMonitors?skip=0&take=3` 和 `GetAllMonitors?skip=3&take=3` 返回完全相同的 10,000 条记录。`skip`/`take` 查询参数被 ABP 动态 Web API 忽略。

**影响**: 已通过提高 `BatchSize` 到 20000 规避，当前可正常工作。但当监视项超过 20,000 时需再次调整或修复 ABP 参数绑定。

**建议**: 在 MonitorCenter 端调查 ABP 动态 API 参数绑定机制。可能的修复方向：
- 添加 `[FromQuery]` 显式绑定
- 使用 `[HttpGet]` 替代默认约定
- 升级 ABP 框架版本

### G6 [信息] GetAllPrerules API 返回 404

**现象**: MonitorCenter 无 `GetAllPrerules` API（HTTP 404），Master 通过 `PreruleDatabaseReader` 直读 PostgreSQL fallback 加载前置规则。

**影响**: 前置规则变更后无法通过 API 自动同步到 Rule Engine，需重启 Master。

**建议**: 在 MonitorCenter 实现 `GetAllPrerules` API，或保持数据库 fallback 作为主通道。

---

## 三、Session 3 遗留问题状态

| 编号 | 状态 | 说明 |
|------|------|------|
| G1 | 仍有效 | GetAllPrerules API 不可用 (404)，数据库 fallback 正常工作 |
| G2 | 已修复 | load-prerules Admin API 已推送至 Workers |
| G3 | 已修复 | gRPC 连接瞬态警告已降级 |
| G4 | 已变化 | 10K 监视项下 100% 监视项产生事件 (vs 之前 95.1%) |
| F6 | 仍存在 | MonitorCenter 分页问题 (见 G5) |

---

## 四、单元测试清单 (77 项全部通过)

与 Session 3 一致，无新增/删除测试。

---

## 五、后续行动

1. 执行异常场景测试 (docs/异常场景测试手册.md)
2. 在 MonitorCenter 修复 GetAllMonitors 分页参数绑定 (G5)
3. 实现 GetAllPrerules API 或确认数据库 fallback 为正式方案 (G6)
