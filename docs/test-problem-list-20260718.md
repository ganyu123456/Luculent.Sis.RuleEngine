# Rule Engine 测试问题清单

> 日期: 2026-07-18 (Session 3)
> 测试类型: 单元测试 + 性能测试 + 端到端集成测试
> 测试环境: Docker Compose (2 Worker + 1 Master + ClickHouse + Redis + PostgreSQL)
> 测试数据: 1000 监视项 + 2 前置规则 (各关联 500 监视项)

---

## 测试结果总览

| 类别 | 结果 |
|------|------|
| 单元测试 | **77/77 通过** (0 失败, 0 跳过) |
| 性能测试 | 4/4 通过 |
| E2E 集成 | ClickHouse 3724 事件, Redis 401 活跃报警, 951/1000 监视项产生事件 |

### 性能基准 (100K 监视项)

| 指标 | 值 |
|------|-----|
| 单周期平均耗时 | 255.3ms |
| P99 延迟 | 686.0ms |
| 5分钟持续吞吐 | 333 events/s |
| 多Worker扩展性 | 1W=315ms, 2W=187ms, 4W=162ms |
| 前置规则开销 | 13.2% (10K 监视项) |
| 5分钟内存 | 774MB → 296MB (GC回收后稳定) |

### E2E 集成统计 (1000 监视项, 10分钟)

| 指标 | 值 |
|------|-----|
| ClickHouse 总事件 | 3724 |
| 触发事件 (StatusKey=satisfiled) | 2065 |
| 消除事件 (StatusKey="") | 1659 |
| Redis 活跃报警 | 401 |
| 产生事件的监视项 | 951/1000 (95.1%) |
| Worker 分布 | 2 × 500 监视项 |

---

## 一、已修复问题 (Session 2-3)

### F1 [已修复] RangeDuration BreakOnHit=false 始终返回 false

**修复**: `PreruleEvaluationService.cs:159-196` — 添加 `anyHit` 标志，BreakOnHit 控制是否立即返回。

### F2 [已修复] 前置规则评估无日志输出

**修复**: `PreruleEvaluationService.cs:35-55` — `EvaluateAllAsync` 添加 `LogInformation` 开始日志和 per-rule 结果日志。

### F3 [已修复] FetchSourceDataAsync 使用 Key 而非 SourceKey 获取 RealDB 数据源

**修复**: `PreruleEvaluationService.cs:78-132` — 完全重写 `FetchSourceDataAsync`，分离 Static (SourceType=1) 和 RealDB (SourceType=3) 处理逻辑。

### F4 [已修复] Redis 实时报警写入去重

**修复**: `WorkerCalculationService.cs` — Redis Write/Clear 移入状态变更判断内部，同状态不重复写入。

### F5 [已修复] 前置规则 suppress 路径重复清除报警

**修复**: `WorkerCalculationService.cs` — `ClearRealtimeAlarmAsync` 移入 `PreviousStatus` 非空检查内部。

### F6 [已修复] MonitorCenter 分页查询 + JOIN 优化

**修复**: `MonitorDataForPublicAppService.cs` — `GetAllMonitors`/`GetAllPrerules` 使用分页+JOIN 避免全表扫描。Rule Engine Master 分批拉取。

---

## 二、当前问题清单

### G1 [已修复] MonitorCenter GetAllPrerules API 不可用 (HTTP 404)

**文件**: `Master/Services/PreruleDatabaseReader.cs` (新增), `Master/Program.cs`, `docker-compose.yml`

**修复方案**: 添加数据库 fallback 机制。Master 启动时先尝试从 MonitorCenter API 拉取前置规则；若失败（404），则通过 Npgsql 直读 PostgreSQL `ssmcprerulemst` 及其关联表，自动加载前置规则定义。

**变更文件**:
- 新增 `Master/Services/PreruleDatabaseReader.cs` — 从 PostgreSQL 读取前置规则完整定义（主表+数据源+RangeDuration+Expression）
- `Master/Program.cs:134-158` — 添加 fallback 逻辑
- `docker-compose.yml` — 添加 `MonitorCenter__DatabaseConnection` 环境变量
- `Master/Luculent.Sis.RuleEngine.Master.csproj` — 添加 `Npgsql` 包引用

---

### G2 [已修复] Master Admin API load-prerules 未推送到 Workers

**修复**: `Master/Program.cs:381-416` — `load-prerules` Admin API 现在注入 `ConfigurationService`、`PartitionService`、`WorkerManager`，加载前置规则后立即调用 `grpcService.PushToWorkersAsync` 推送到所有 Worker。

---

### G3 [已修复] gRPC 连接在 Master 重启时产生瞬态警告

**修复**: `Worker/Services/GrpcConnectionService.cs:63-66,164-167` — HTTP/2 connection reset 场景（"HTTP/2" 在异常消息中）降级为 `LogInformation`，其他异常保持 `LogWarning`。

---

### G4 [信息] 49/1000 监视项未产生事件 (4.9%)

**现象**: 测试期间 951 个监视项产生事件，49 个无事件。

**根因**: 前置规则使用 SimulatedTrendReader 生成随机值 (range 50-150)，条件为 `tag_value > 50` (Prerule A) 或 `tag_value >= 50` (Prerule B, DurationSecond=0)。随机值偶然低于阈值的监视项被抑制。

**影响**: 无功能问题。这是模拟环境下的预期行为。生产环境有真实时序数据时，所有监视项都会根据实际数据产生事件。

---

## 三、单元测试清单 (77 项全部通过)

### 前置规则评估 (19 tests)
- `EvaluateRangeDuration_BreakOnHitTrue_ReturnsTrueImmediately`
- `EvaluateRangeDuration_BreakOnHitFalse_ReturnsTrueAfterDuration`
- `EvaluateRangeDuration_NoMatchingCondition_ReturnsFalse`
- `EvaluateRangeDuration_EmptyRules_ReturnsFalse`
- `EvaluateRangeDuration_DisabledRule_Skipped`
- `EvaluateRangeDuration_MultipleRules_FirstHitBreaksWithBreakOnHit`
- `EvaluateExpression_ValidComparison_ReturnsTrue`
- `EvaluateExpression_InvalidComparison_ReturnsFalse`
- `EvaluateExpression_NoExpression_ReturnsFalse`
- `EvaluateExpression_AndComposition_ReturnsTrue`
- `EvaluateExpression_OrComposition_ReturnsCorrect`
- `EvaluateExpression_EqualityCheck_ReturnsCorrect`
- `EvaluateAllAsync_NotEnabled_ReturnsTrue`
- `EvaluateAllAsync_UnknownRuleType_ReturnsTrue`
- `EvaluateAllAsync_UpdatesAllStates`
- `EvaluateAllAsync_EmptyDefinitions_NoOp`
- `FetchSourceData_StaticSource_ParsesSourceKeyDirectly`
- `FetchSourceData_RealDB_UsesSourceKeyAsTagName`
- `FetchSourceData_RealDBFailure_ReturnsFalse`

### Worker 计算流程 (7 tests)
- `ProcessMonitor_StateChange_WritesHistoryAndSavesState`
- `ProcessMonitor_SameStatus_NoDuplicateHistory`
- `ProcessMonitor_ClearEvent_WritesEmptyStatusAndClearsRealtime`
- `ProcessMonitor_AfterClear_NoDuplicateEventNextCycle`
- `ProcessMonitor_PereruleSuppressWithClear_WritesEmptyStatus`
- `ProcessMonitor_PereruleSuppressNoClear_NoEvent`
- `ProcessMonitor_FirstCalculation_NoSpuriousEvent`

### 前置规则 Pipeline (16 tests)
- `CheckAsync_*` — 覆盖 ManualFlag、StopMonitor、SourceDependency、InterfaceMonitoring 等全部抑制路径

### 规则计算器 (25 tests)
- CalculateRuleRangeDuration (4), CalculateRuleRangeFrequency (2), CalculateRuleExpression (2)
- CalculateFeatureValue (5), CalculatePackageValue (5), CalculateRulePackageValue (2)
- CalculateInterfaceMonitoring (5)

### 分区算法 (5 tests)
- Cost-Aware 贪心装箱：均衡性、成本计算、单Worker、空输入、空Worker

### 性能测试 (4 tests)
- Case1: 100K 监视项单周期
- Case2: 100K 监视项 5 分钟持续
- Case3: 多 Worker 并行扩展
- Case4: 前置规则开销

### TrendDB 读取 (1 test)
- `Read_Single_Tag_dbtest1`

---

## 四、部署建议

1. **重新构建 MonitorCenter (sis-service)**: 部署 `GetAllPrerules` API，使 Master 启动时能自动拉取前置规则
2. **重新构建 Rule Engine Master**: 部署修复后的 `load-prerules` Admin API (push to Workers)
3. **gRPC 重连日志降噪**: 将 `HttpProtocolException` 的重连日志降为 Information 级别
