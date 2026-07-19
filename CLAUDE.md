# CLAUDE.md — Rule Engine 项目开发指南

## 项目概览

本仓库是 Luculent.Sis.RuleEngine，一个 .NET 6 规则引擎，负责从 MonitorCenter 同步监视项配置，从 TrendDB 拉取实时数据，执行规则计算，并将告警事件写入 Redis（实时）和 ClickHouse（历史）。

### 代码结构

```
src/
├── Luculent.Sis.RuleEngine.Master/     # Master 节点 — API、分区、配置同步
│   ├── Services/
│   │   ├── HistoryAlarmService.cs       # ClickHouse 历史查询
│   │   ├── AlarmQueryService.cs         # Redis 实时查询
│   │   ├── MonitorCenterClient.cs       # MonitorCenter HTTP 客户端
│   │   └── PreruleDatabaseReader.cs     # PostgreSQL 直读 fallback
│   └── Program.cs
├── Luculent.Sis.RuleEngine.Worker/     # Worker 节点 — 计算引擎
│   ├── WorkerCalculationService.cs      # 核心计算循环 + 状态跟踪
│   ├── DataSource/
│   │   └── SimulatedTrendReader.cs      # 开发/测试用正弦波模拟器
│   ├── Storage/
│   │   ├── ClickHouseAlarmWriter.cs     # ClickHouse 批量写入
│   │   ├── RedisAlarmWriter.cs          # Redis 实时写入
│   │   └── InMemoryAlarmWriter.cs       # 测试用内存存储
│   └── Calculation/
│       ├── RuleDispatcher.cs
│       └── Calculators/
│           ├── CalculateRuleExpression.cs
│           └── CalculateRuleRangeDuration.cs
└── Luculent.Sis.RuleEngine.Shared/     # 共享模型/DTO
    ├── Models/
    │   ├── AlarmModels.cs               # AlarmEvent, AlarmSnapshot
    │   └── CalculationState.cs          # 状态跟踪（含 MaxValue/MinValue）
    └── DTOs/AlarmDTOs.cs
```

### 关联项目

| 项目 | 路径 | 角色 |
|------|------|------|
| MonitorCenter | `../00-Luculent.Sis.MonitorCenter/Luculent.Sis.MonitorCenter/` | ABP 前端后端，分布式模式下代理到 Rule Engine |
| SIS.Service | `../00-LiEMS_AIcode/SIS.Service/` | LiEMS 主应用容器 |
| Plugins 目录 | `../00-LiEMS_AIcode/SIS.Service/Plugins/` | MonitorCenter DLL 部署目标 |

## 环境

### Docker 容器

| 容器 | 端口 | 用途 |
|------|------|------|
| `ruleengine-master` | 11082 (HTTP), 11083 (gRPC) | Master API + 分区服务 |
| `*-worker-1`, `*-worker-2` | — | Worker 计算节点 |
| `ruleengine-clickhouse` | 8123, 9000 | ClickHouse 历史数据库 |
| `ruleengine-redis` | 6379 | Redis 实时告警 |
| `sis-postgres` | 5432 | PostgreSQL (sis1 数据库) |
| `sis-service` | 11080 | LiEMS 主应用 (含 MonitorCenter) |

### 关键配置

- ClickHouse 连接: `Host=clickhouse;Port=8123;Database=ruleengine;Username=ruleengine;Password=RuleEngine2026!`
- PostgreSQL: `Host=postgres;Port=5432;Username=postgres;Password=luculent1!;Database=sis1`
- 分布式模式需要: `MonitorCenter__Distributed__Enabled=true`, `MonitorCenter__Distributed__RuleEngineUrl=http://host.docker.internal:11082`

## 认证

MonitorCenter API 需要 ABP Cookie + JWT 认证：

```bash
# 获取 Token
curl -s -c /tmp/cookies.txt http://127.0.0.1:11080/
XSRF=$(grep XSRF-TOKEN /tmp/cookies.txt | awk '{print $NF}')
JWT=$(curl -s -b /tmp/cookies.txt \
    -H "Content-Type: application/json" \
    -H "RequestVerificationToken: $XSRF" \
    -d '{"userKey":"admin","tenantKey":"Self","authenticateFrom":"Self","language":"zh-Hans","platform":null}' \
    http://127.0.0.1:11080/api/services/app/TokenAuth/Authenticate \
    | python3 -c "import json,sys; d=json.load(sys.stdin); print(d['result']['accessToken'])")
echo "$JWT" > /tmp/jwt_token.txt

# 后续请求携带
# -H "Authorization: Bearer $JWT"
```

## 构建与部署

### Rule Engine

```bash
cd 03-Luculent.Sis.RuleEngine
dotnet build && docker compose build && docker compose up -d
```

### MonitorCenter → sis-service

**每次修改 MonitorCenter 代码后必须执行以下步骤：**

```bash
# 1. Build
cd 00-Luculent.Sis.MonitorCenter
dotnet build Luculent.Sis.MonitorCenter/Luculent.Sis.MonitorCenter.csproj -c Release

# 2. 拷贝 DLL 到容器 (Plugins 不是挂载卷，必须用 docker cp)
docker cp Luculent.Sis.MonitorCenter/bin/Release/net6.0/Luculent.Sis.MonitorCenter.dll \
   sis-service:/app/Plugins/Luculent.Sis.MonitorCenter.dll

# 3. 重启容器
docker restart sis-service
```

**注意:** Plugins 目录不是 Docker volume，`docker restart` 不会自动加载宿主机 Plugins 目录的新 DLL。必须用 `docker cp` 拷贝。

**忘记步骤 2 会导致 sis-service 使用旧 DLL，所有 MonitorCenter 修改丢失。**

## 架构关键点

### 事件流模型

- ClickHouse 只写入 trigger 事件（`status_key != ""`）和 clear 事件（`status_key = ""`）
- 没有 UPDATE 操作，只有 INSERT
- 历史查询在查询侧用窗口函数配对 trigger + clear 事件
- `MaxValue`/`MinValue` 是"上一状态段内的极值"，由 clear 事件携带

### 状态跟踪

- `CalculationState` 通过 RocksDB 持久化，JSON 序列化
- `[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]` 意味着值为 0 的 double 字段不会被序列化
- 首次创建的状态 MaxValue=0/MinValue=0，存储在 RocksDB 时不写入这些字段

### MaxValue/MinValue 数据流

```
WorkerCalculationService.ComputeMonitor
  → CalculationState.MaxValue/MinValue (运行极值跟踪)
  → MonitorTransition.PrevMax/PrevMin (状态迁移时捕获)
  → AlarmEvent.MaxValue/MinValue (写入 ClickHouse)
  → HistoryAlarmService.QueryAsync (读取)
  → MonitorCenter HistoryMonitor/GetAllForList (配对展示)
```

## SimulatedTrendReader

开发/测试环境使用正弦波模拟器生成测点数据：

- **普通 tag**: 正弦波 0-200，独立频率/相位/振幅分布于 0-200 范围
- **`feat_` 前缀 tag**: 生成离散整数 1/2/3（每 15s 切换），用于 FeatureValue 规则 TriggerValueDefDic 匹配
- FeatureValue 测试数据 source alias 必须以 `feat_` 前缀开始，否则正弦波值极少命中 {1,2,3}

## 常见陷阱

1. **IN 子句过大** — ClickHouse `max_query_size` ≈ 256KB，monitorKeys IN 子句不要超过 5000 项
2. **Expression 计算器不设 TriggerValue** — 导致 MaxValue/MinValue 始终为 0
3. **状态序列化丢失** — `WhenWritingDefault` 导致值为 0 的 double 字段不持久化
4. **MonitorCenter DLL 过期** — 必须用 `docker cp` 拷贝 DLL 到容器，Plugin 目录不是挂载卷
5. **API 路由前缀** — 是 `/api/services/monitorcenter/`，不是 `/api/services/app/`
6. **FocusSourceId 映射** — MonitorDataForPublicAppService 必须返回 source alias 而非 source_no
7. **TriggerValueDefDic 构建** — 仅当所有 `statuslin_trigger > 0` 且唯一时才构建 dict
8. **MultiStateRule Conditions** — 需按 TagName 分组 ssmcrulemulstarandurmst 行来填充
9. **rule_type 列** — AlarmEvent.RuleType 必须从 MonitorConfig.RuleType 填充，ClickHouse FormatRow 不再硬编码 0
