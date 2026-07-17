# Monitor Center 服务启动调试方法

## 架构概览

```
SIS 主项目 (LiEMS_AIcode)
├── SIS.Service/                  ← 主服务目录 (Docker 容器内 /app)
│   ├── Luculent.Sis.Web.dll      ← 主 Host 进程
│   ├── appsettings.json          ← 主配置文件 (含 MonitorCenter:Distributed)
│   └── Plugins/                  ← 插件目录 (Monitor Center 等模块)
│       ├── Luculent.Sis.MonitorCenter.dll
│       ├── Luculent.Sis.MonitorCenter.Shared.dll
│       └── ...
├── docker-compose.yml            ← sis-service + postgres + mongodb
└── ...

Rule Engine 项目 (03-Luculent.Sis.RuleEngine)
├── docker-compose.yml            ← Master + Workers + Redis + ClickHouse
├── src/
│   ├── Luculent.Sis.RuleEngine.Master/     → HTTP API (11082)
│   ├── Luculent.Sis.RuleEngine.Worker/     → 计算节点 (gRPC)
│   ├── Luculent.Sis.RuleEngine.Shared/     → 共享 DTO/Model
│   └── Luculent.Sis.RuleEngine.TrendDb/   → TrendDB 数据读取类库
└── ...

Monitor Center 项目 (00-Luculent.Sis.MonitorCenter)
├── Luculent.Sis.MonitorCenter/             → 编译产物 → Plugins/
└── Luculent.Sis.MonitorCenter.Shared/      → 编译产物 → Plugins/
```

## 关键配置

### SIS.Service/appsettings.json

```json
{
  "MonitorCenter": {
    "Distributed": {
      "Enabled": true,                              // true = 规则引擎接管计算
      "RuleEngineUrl": "http://master:11082",        // 规则引擎 Master 地址
      "DbType": "ClickHouse",
      "Comment": "启用分布式模式时，计算交给规则引擎，监视项变更自动推送到 RuleEngineUrl"
    }
  }
}
```

- `Enabled: false` → Monitor Center 本地计算引擎运行，不推送规则引擎
- `Enabled: true` → 本地计算引擎停止，实体变更推送到规则引擎，实时/历史报警查询代理到规则引擎

### 容器网络注意事项

- `sis-service` 默认在 `00-liems_aicode_default` 网络 (172.18.0.0/16)
- `ruleengine-master` 在 `03-luculentsisruleengine_ruleengine` 网络 (172.28.0.0/16)
- **两个容器在不同 Docker 网络，默认无法互通！** 需要手动连接:

```bash
docker network connect 03-luculentsisruleengine_ruleengine sis-service
```

- 连接后 `sis-service` 可通过 `master` 容器名访问 Rule Engine (172.28.0.4:11082)

---

## 完整调试流程

### Step 1: 启动基础设施

```bash
# 1. 启动 SIS 服务 (PostgreSQL + MongoDB + SIS)
cd /Users/ganyu/Desktop/myself/00-SIS/00-LiEMS_AIcode
docker compose up -d

# 2. 启动 Rule Engine (Master + Worker × 3 + Redis + ClickHouse)
cd /Users/ganyu/Desktop/myself/00-SIS/05-code/00-myself/03-Luculent.Sis.RuleEngine
docker compose up -d

# 3. 确认所有服务 healthy
docker ps --format "table {{.Names}}\t{{.Status}}"
# sis-service           Up (healthy)
# ruleengine-master     Up (healthy)
# 03-luculentsisruleengine-worker-1  Up (healthy)
# ruleengine-redis      Up (healthy)
# ruleengine-clickhouse Up (healthy)
# ...
```

### Step 2: 连接容器网络 (首次/容器重建后需要)

```bash
# SIS 服务需要能访问 Rule Engine Master
docker network connect 03-luculentsisruleengine_ruleengine sis-service

# 验证连接
docker exec sis-service sh -c "getent hosts master"
# 应输出: 172.28.0.4  master
```

### Step 3: 配置 RuleEngineUrl

在 `appsettings.json` 中使用容器名而不是 `host.docker.internal`:

```json
"RuleEngineUrl": "http://master:11082"
```

> **为什么不用 `host.docker.internal`？** Docker Desktop Mac 将其解析为 IPv6 地址 (`fdc4:f303:9324::254`)，而 .NET 的 `ASPNETCORE_URLS=http://0.0.0.0:11082` 只监听 IPv4，导致 IPv6 连接被拒绝。

### Step 4: 编译 Monitor Center

```bash
cd /Users/ganyu/Desktop/myself/00-SIS/05-code/00-myself/00-Luculent.Sis.MonitorCenter

# 编译 (dotnet build 即可，不需要 publish)
dotnet build Luculent.Sis.MonitorCenter/Luculent.Sis.MonitorCenter.csproj -c Release
```

### Step 5: 部署 DLL 到容器

**关键：Plugins 目录不是 volume mount，必须用 `docker cp` 直接拷贝到容器内！**

```bash
# 拷贝主 DLL
docker cp Luculent.Sis.MonitorCenter/bin/Release/net6.0/Luculent.Sis.MonitorCenter.dll \
    sis-service:/app/Plugins/Luculent.Sis.MonitorCenter.dll

# 拷贝 Shared DLL
docker cp Luculent.Sis.MonitorCenter/bin/Release/net6.0/Luculent.Sis.MonitorCenter.Shared.dll \
    sis-service:/app/Plugins/Luculent.Sis.MonitorCenter.Shared.dll

# 如果修改了 appsettings.json，也需要拷贝
docker cp /Users/ganyu/Desktop/myself/00-SIS/00-LiEMS_AIcode/SIS.Service/appsettings.json \
    sis-service:/app/appsettings.json
```

> **注意**: 不能用 `cp` 到主机的 Plugins 目录！SIS 容器没有 volume mount，必须直接用 `docker cp`。

### Step 6: 重启 SIS 服务

```bash
docker restart sis-service

# 等待启动完成
until curl -s -o /dev/null -w "%{http_code}" http://localhost:11080/ | grep -q "200"; do sleep 2; done
echo "SIS Service Ready"
```

### Step 7: 验证分布式模式

**a) 确认日志中有分布式模式提示:**

```bash
docker logs sis-service --tail 50 | grep "分布式"
# 应输出: >>监视中心---分布式模式已启用，本地计算引擎不启动
```

**b) 验证 Rule Engine 健康状态:**

```bash
curl http://localhost:11082/api/ruleengine/health | python3 -m json.tool
# {
#     "status": "healthy",
#     "monitorCount": 1001,
#     "activeWorkers": 3,
#     "workerDistribution": { ... }
# }
```

**c) 验证实时报警查询代理:**

```bash
# 前置条件: Rule Engine 中有实时报警数据
# Token 格式: 直接使用签发 Token，不加 "Bearer" 前缀

TOKEN="ss-a5f8bd482c3e422c86026dcae470f817"

# 测试实时报警 (只显示有报警的监视项)
curl -s -X POST "http://127.0.0.1:11080/api/services/monitorcenter/RealMonitor/GetAllForList" \
    -H "Content-Type: application/json" \
    -H "Authorization: $TOKEN" \
    -d '{"Filter":"","MonitorItemIds":[],"ShowAll":false,"SkipCount":0,"MaxResultCount":10}' \
    | python3 -m json.tool

# 测试实时报警 (显示所有监视项，包括无报警的)
curl -s -X POST "http://127.0.0.1:11080/api/services/monitorcenter/RealMonitor/GetAllForList" \
    -H "Content-Type: application/json" \
    -H "Authorization: $TOKEN" \
    -d '{"Filter":"","MonitorItemIds":[],"ShowAll":true,"SkipCount":0,"MaxResultCount":10}' \
    | python3 -m json.tool
```

**d) 验证历史报警查询代理:**

```bash
curl -s -X POST "http://127.0.0.1:11080/api/services/monitorcenter/HistoryMonitor/GetAllForList" \
    -H "Content-Type: application/json" \
    -H "Authorization: $TOKEN" \
    -d '{"Filter":"","MonitorItemIds":[],"SkipCount":0,"MaxResultCount":10}' \
    | python3 -m json.tool
```

**e) 验证 GetRealEvent (旧代理，作为连通性基线):**

```bash
curl -s "http://127.0.0.1:11080/api/services/monitorcenter/MonitorDataForPublic/GetRealEvent" \
    -H "Authorization: $TOKEN" \
    | python3 -c "import json,sys; d=json.load(sys.stdin); print(f'事件数: {len(d[\"result\"])}')"
```

---

## 常见问题排查

### 1. 实时报警查询返回 0 条

**症状**: `GetAllForList` 返回 `totalCount: 0`

**可能原因 A**: 分布式模式下 `MonitorItemStore` 缓存为空
- 分布式模式下 `RuntimeManager.Initialize()` 被跳过，`MonitorItemStore` 不会从数据库加载监视项
- **解决**: 代理方法中改用 `IRepository<MonitorItem>.GetAll()` 直接从数据库读取（已修复）

**可能原因 B**: Rule Engine 报警的 `monitorKey` 与 Monitor Center 的 `Key` 不匹配
- Rule Engine 报警使用内部 ID/Key，Monitor Center 使用数据库中的 ID/Key
- **解决**: 用 `monitorKey` 而不是 `monitorId` 匹配（已修复）

**可能原因 C**: Rule Engine 本身没有报警数据
- Worker 需要持续运行才会产生实时报警

**验证方法**:
```bash
# 直接查询 Rule Engine
curl http://localhost:11082/api/ruleengine/alarms/realtime | python3 -c \
  "import json,sys; d=json.load(sys.stdin); print(f'报警数: {len(d[\"items\"])}')"
```

### 2. 历史报警查询返回 0 条

**可能原因 A**: ClickHouse 中 `alarm_events` 表不存在
- Rule Engine 历史查询返回 500: `UNKNOWN_TABLE`
- **解决**: 需要创建 ClickHouse 表结构

```bash
# 验证
curl -s -X POST "http://localhost:11082/api/ruleengine/alarms/history" \
    -H "Content-Type: application/json" \
    -d '{"MonitorIds":[],"SkipCount":0,"MaxResultCount":3}'
```

### 3. 容器网络不通

**症状**: GetRealEvent 返回空或超时

**验证**:
```bash
# 检查 sis-service 能否解析 master
docker exec sis-service sh -c "getent hosts master"
# 如果无输出 → 网络未连接

# 检查 sis-service 所在的网络
docker inspect sis-service --format '{{range $k,$v := .NetworkSettings.Networks}}{{$k}} {{end}}'
# 应同时包含: 00-liems_aicode_default 和 03-luculentsisruleengine_ruleengine

# 如果缺少 ruleengine 网络，连接它:
docker network connect 03-luculentsisruleengine_ruleengine sis-service
```

### 4. DLL 未生效

**症状**: 修改代码后端点行为无变化

**原因**: 
- Plugins 目录不是 volume mount，不能从主机 `cp` 
- DLL 可能被 SIS 加载到临时目录 (shadow copy)

**验证 DLL 已更新**:
```bash
# 检查容器内 DLL 时间戳
docker exec sis-service ls -la /app/Plugins/Luculent.Sis.MonitorCenter.dll

# 找到所有 DLL 副本
docker exec sis-service find / -name "Luculent.Sis.MonitorCenter.dll" 2>/dev/null
```

### 5. 配置未生效

**症状**: 分布式模式检查不通过

**验证配置已加载**:
```bash
# 检查容器内配置
docker exec sis-service grep -A3 "Distributed" /app/appsettings.json

# 检查分布式模式启动日志
docker logs sis-service --tail 100 | grep -i "分布式\|distributed"
```

---

## 架构要点

### 分布式代理的代码位置

| 文件 | 方法 | 代理目标 |
|------|------|----------|
| `MonitorDataForPublicAppService.cs` | `GetRealEvent()` | `GET /api/ruleengine/alarms/realtime` |
| `RealMonitorAppService.cs` | `GetAllForList()` | `GET /api/ruleengine/alarms/realtime` |
| `HistoryMonitorAppService.cs` | `GetAllForList()` | `POST /api/ruleengine/alarms/history` |
| `RuleEngineClient.cs` | `NotifyChangedAsync()` | `POST /api/ruleengine/monitors/on-changed` |

### ID/Key 匹配关系

| 系统 | ID 字段 | Key 字段 | 示例 |
|------|---------|----------|------|
| Monitor Center DB | `Id` (MongoDB ObjectId) | `Key` (用户定义) | `3a22829c...` / `FULL-TEST-001` |
| Rule Engine 报警 | `monitorId` (内部) | `monitorKey` (用户定义) | `mon-006014` / `mon_key_006014` |

**匹配策略**: 使用 `monitorKey` ↔ `Key` 匹配，不是 `monitorId` ↔ `Id`！两者 ID 体系不同。

### 认证 Token

SIS 使用 `TokenListFilterMiddleware` 接受特定格式的 Token:
```bash
# 正确的 Header 格式 (无 "Bearer" 前缀)
Authorization: ss-a5f8bd482c3e422c86026dcae470f817
```

### ForceUseAdmin

appsettings.json 中设置 `"ForceUseAdmin": true` 可绕过 JWT 验证实现超级管理员登录。
