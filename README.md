# Luculent.Sis.RuleEngine

基于 .NET 的分布式规则引擎，从 MonitorCenter 同步监视项配置，拉取实时数据，执行规则计算，并将告警事件写入 Redis（实时）和 ClickHouse（历史）。

## 架构

```
MonitorCenter (PostgreSQL) → Master (HTTP + gRPC) → Workers (计算节点)
                                                         ↓
                                                    Redis (实时告警)
                                                         ↓
                                                    ClickHouse (历史事件)
```

- **Master**: 配置同步、分区分配、API 网关（端口 11082 HTTP / 11083 gRPC）
- **Worker**: 数据采集 (1s) → TagValueStore → 规则计算 (1s) → 事件写入
- **分区策略**: Cost-Aware 贪心装箱，不均衡度 < 0.1%

## 支持的规则类型

| RuleType | 规则类型 | 说明 |
|----------|----------|------|
| 1 | Expression | 表达式规则（动态编译，支持 Math 函数） |
| 2 | RangeDuration | 区间时长规则（Tag 值超过阈值持续 N 秒触发） |
| 3 | RangeFrequency | 区间频率规则 |
| 4 | FeatureValue | 特征值规则（离散值 TriggerValueDefDic 匹配） |
| 5 | PackageValue | 打包点规则（位与运算匹配） |
| 6 | WallTemperature | 壁温规则 |
| 7 | InterfaceMonitoring | 接口监视规则 |
| 8 | RulePackageValue | 多打包点规则 |
| 9 | MultiStateRangeDuration | 多状态区间时长规则 |

## 技术栈

| 组件 | 技术 |
|------|------|
| 运行时 | .NET / C# |
| 实时存储 | Redis (StackExchange.Redis) |
| 历史存储 | ClickHouse (ClickHouse.Client) |
| 配置存储 | PostgreSQL (Npgsql) |
| 进程间通信 | gRPC (protobuf) |
| 容器化 | Docker Compose |
| 状态持久化 | RocksDB |

## 项目结构

```
src/
├── Luculent.Sis.RuleEngine.Master/     # Master 节点
│   ├── Services/
│   │   ├── ConfigurationService.cs      # 全量配置管理
│   │   ├── PartitionService.cs          # Cost-Aware 分区
│   │   ├── GrpcConnectionService.cs     # Worker gRPC 通道
│   │   ├── MonitorCenterClient.cs       # MonitorCenter HTTP 客户端
│   │   ├── HistoryAlarmService.cs       # ClickHouse 历史查询
│   │   ├── AlarmQueryService.cs         # Redis 实时查询
│   │   └── PreruleDatabaseReader.cs     # PostgreSQL 直读 fallback
│   └── Program.cs
├── Luculent.Sis.RuleEngine.Worker/     # Worker 节点
│   ├── WorkerCalculationService.cs      # 核心计算循环
│   ├── DataAcquisition/
│   │   └── DataAcquisitionService.cs    # 数据采集 (1s 周期)
│   ├── DataSource/
│   │   └── SimulatedTrendReader.cs      # 开发/测试用正弦波模拟器
│   ├── Calculation/
│   │   ├── RuleDispatcher.cs
│   │   ├── PreruleEvaluationService.cs
│   │   └── Calculators/                 # 9 种规则计算器
│   └── Storage/
│       ├── ClickHouseAlarmWriter.cs     # ClickHouse 批量写入
│       ├── RedisAlarmWriter.cs          # Redis 实时写入
│       └── InMemoryAlarmWriter.cs       # 测试用内存存储
└── Luculent.Sis.RuleEngine.Shared/     # 共享模型/DTO/接口
    ├── Models/
    │   ├── AlarmModels.cs               # AlarmEvent, AlarmSnapshot
    │   ├── MonitorConfig.cs             # 监视项配置模型
    │   └── CalculationState.cs          # 状态跟踪 (MaxValue/MinValue)
    └── DTOs/
```

## 快速开始

### 环境要求

- .NET SDK
- Docker & Docker Compose
- PostgreSQL (含 MonitorCenter 数据库)

### 构建与部署

```bash
# 1. 构建项目
dotnet build

# 2. 运行单元测试
dotnet test

# 3. 构建 Docker 镜像并启动
docker compose build
docker compose up -d

# 4. 查看运行状态
docker compose ps
docker logs ruleengine-master --tail 20
```

### 加载测试数据

```bash
# 执行 120K 监视项测试数据
docker exec sis-postgres psql -U postgres -d sis1 \
  -f /path/to/deploy/test-data-120k-production.sql

# 通知 Master 重新同步配置
curl -X POST http://localhost:11082/api/ruleengine/sync/full
```

## 关键 API

| 端点 | 方法 | 说明 |
|------|------|------|
| `/api/ruleengine/alarms/realtime` | GET | 全量实时报警 |
| `/api/ruleengine/alarms/history` | POST | 历史报警查询（分页/过滤） |
| `/api/ruleengine/alarms/history/closed-loop/validate` | GET | 闭环完整性验证 |
| `/api/ruleengine/sync/full` | POST | 触发全量配置同步 |
| `/api/ruleengine/prerules/states` | GET | 前置规则评估状态 |

## 数据流

```
TrendDB / Simulator → DataAcquisitionService (1s)
                           ↓
                      TagValueStore (缓存)
                           ↓
                   WorkerCalculationService (1s)
                    Phase 1: CPU 并行计算
                    Phase 2: 批量 I/O 写入
                      ↓              ↓
                 Redis (实时)    ClickHouse (历史)
```

### 事件流模型

- 状态变更时写入事件（`newStatus != PreviousStatus`）
- Trigger 事件: `status_key != ""`
- Clear 事件: `status_key = ""`
- 无 UPDATE，只有 INSERT
- 查询侧用窗口函数配对 trigger + clear 事件

## 性能基准 (120K 监视项, 2 Workers)

| 指标 | 值 |
|------|-----|
| 总吞吐 | ~3,200 events/s |
| Worker CPU | ~165%/Worker (2 核限制) |
| Worker 内存 | ~1.1 GB/Worker |
| ClickHouse 写入 | ~5,000 条/批, 100-600ms/批 |
| 计算周期 | Phase 1 ~490ms, Phase 2 ~100-400ms |
| gRPC 配置推送 | 分块传输, 15K/块 |

## 单元测试

```bash
dotnet test
# 预期: 110 passed, 0 failed, 0 skipped
```

## 版本

| Tag | 日期 | 里程碑 |
|-----|------|--------|
| v1.5.0 | 2026-07-19 | 120K 规模 6 种规则类型全部验证通过 |
| v1.4.0 | 2026-07-19 | MaxValue/MinValue 全链路追踪 |
| v1.3.0 | 2026-07-18 | 前置规则 + gRPC 分块推送 |
