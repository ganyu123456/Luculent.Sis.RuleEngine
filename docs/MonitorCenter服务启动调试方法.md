# Monitor Center 服务启动调试方法

## 架构概览

```
SIS 主项目 (LiEMS_AIcode)
├── SIS.Service/                  ← 主服务目录 (ABP Host + Plugins)
│   ├── Luculent.Sis.Web.dll      ← 主 Host 进程
│   ├── appsettings.json          ← 主配置文件 (含 MonitorCenter:Distributed)
│   └── Plugins/                  ← 插件目录 (Monitor Center 等模块)
│       ├── Luculent.Sis.MonitorCenter.dll
│       ├── Luculent.Sis.MonitorCenter.Shared.dll
│       └── ...
├── docker-compose.yml            ← 容器编排 (sis-service + postgres + mongodb)
└── ...

开发项目 (05-code/00-myself)
├── 00-Luculent.Sis.MonitorCenter/ ← Monitor Center 插件源码
│   ├── Luculent.Sis.MonitorCenter/          → 编译产物 → Plugins/
│   └── Luculent.Sis.MonitorCenter.Shared/   → 编译产物 → Plugins/
└── 01-luculent.sis.netcore/      ← SIS 主框架源码 (一般不改)
```

## 涉及项目

| 项目 | 路径 | 何时需要重新编译 |
|------|------|-----------------|
| **Monitor Center 主模块** | `00-Luculent.Sis.MonitorCenter/Luculent.Sis.MonitorCenter/` | 修改了 MonitorCenter 业务代码，如 `RuntimeManager`, `MonitorItemChangeEventHandler`, `MonitorDataForPublicAppService`, `RuleEngineClient` 等 |
| **Monitor Center Shared** | `00-Luculent.Sis.MonitorCenter/Luculent.Sis.MonitorCenter.Shared/` | 修改了 `MonitorCenterConsts.cs` 等共享代码 |
| **SIS 主框架** | `01-luculent.sis.netcore/src/Luculent.Sis.Web/` | 修改了 SIS 框架代码或 appsettings.json |

## 关键开关

Monitor Center 与 Rule Engine 的联动通过 `SIS.Service/appsettings.json` 中的配置控制：

```json
{
  "MonitorCenter": {
    "Distributed": {
      "Enabled": false,           // true = 启用规则引擎联动
      "DbType": "ClickHouse",
      "RuleEngineUrl": "http://localhost:11081"  // 规则引擎 Master 地址
    }
  }
}
```

- `Enabled: false` → Monitor Center 本地计算引擎运行，不推送规则引擎
- `Enabled: true` → 本地计算引擎停止，实体变更推送到规则引擎，实时/历史报警查询代理到规则引擎

---

## 完整调试流程

### 1. 确保基础设施就绪

```bash
# 进入部署目录
cd /Users/ganyu/Desktop/myself/00-SIS/00-LiEMS_AIcode

# 启动数据库 + SIS 主服务
docker compose up -d

# 确认服务运行正常
docker compose ps
# 应看到: sis-postgres, sis-mongodb, sis-rpc-service, sis-service 均为 healthy/running
```

### 2. 修改 Monitor Center 代码

在 `00-Luculent.Sis.MonitorCenter/` 中修改代码后：

```bash
cd /Users/ganyu/Desktop/myself/00-SIS/05-code/00-myself/00-Luculent.Sis.MonitorCenter

# 编译 Monitor Center (Release 模式)
dotnet publish Luculent.Sis.MonitorCenter/Luculent.Sis.MonitorCenter.csproj \
    -c Release \
    -f net6.0 \
    -o /tmp/monitorcenter-publish

# 编译 Monitor Center Shared
dotnet publish Luculent.Sis.MonitorCenter.Shared/Luculent.Sis.MonitorCenter.Shared.csproj \
    -c Release \
    -f net6.0 \
    -o /tmp/monitorcenter-shared-publish
```

### 3. 部署到 SIS.Service/Plugins

```bash
PLUGINS_DIR="/Users/ganyu/Desktop/myself/00-SIS/00-LiEMS_AIcode/SIS.Service/Plugins"

# 复制 Monitor Center 主模块 (dll + pdb + deps.json + runtimeconfig.json + xml)
cp /tmp/monitorcenter-publish/Luculent.Sis.MonitorCenter.* "$PLUGINS_DIR/"
cp /tmp/monitorcenter-publish/Luculent.Sis.MonitorCenter.Migrations.* "$PLUGINS_DIR/"

# 复制 Monitor Center Shared
cp /tmp/monitorcenter-shared-publish/Luculent.Sis.MonitorCenter.Shared.* "$PLUGINS_DIR/"
```

### 4. 修改配置（如需要）

如果调整了分布式模式开关：

```bash
# 编辑 SIS 主配置
vim /Users/ganyu/Desktop/myself/00-SIS/00-LiEMS_AIcode/SIS.Service/appsettings.json

# 找到 MonitorCenter:Distributed 段，修改:
#   "Enabled": true   ← 启用规则引擎联动
#   "RuleEngineUrl": "http://localhost:11081"  ← 指向规则引擎 Master
```

### 5. 重启 SIS 服务

```bash
cd /Users/ganyu/Desktop/myself/00-SIS/00-LiEMS_AIcode

# 重启 sis-service 容器
docker compose restart sis-service

# 查看日志确认启动
docker compose logs -f sis-service
```

### 6. 验证联动

**验证 Monitor Center → Rule Engine 推送：**
1. 在 Monitor Center 中新增/修改/删除一个监视项
2. 查看规则引擎 Master 日志，应收到 `POST /api/ruleengine/monitors/on-changed`
3. 检查规则引擎健康端点: `curl http://localhost:11081/api/ruleengine/health`

**验证报警查询代理：**
- 当 `Distributed.Enabled: true` 时，Monitor Center 的实时报警和历史报警查询会代理到规则引擎
- 查看 sis-service 日志确认请求转发了

---

## 调试技巧

### 本地调试 Monitor Center (非 Docker)

Monitor Center 的 launchSettings.json 中 `ganyu` profile 配置为使用本地 SIS.Service 目录启动：

```json
{
  "executablePath": "C:\\data\\LiEMS7.0_SIS_Index\\00-平台发布版本\\SIS.Service\\Luculent.Sis.Web.exe",
  "workingDirectory": "C:\\data\\LiEMS7.0_SIS_Index\\00-平台发布版本\\SIS.Service"
}
```

macOS 上调试：直接在 Rider/VS Code 中设置启动参数，Working Directory 指向 `SIS.Service/`，启动程序为 `dotnet Luculent.Sis.Web.dll`。

### 查看规则引擎状态

```bash
# 健康检查
curl http://localhost:11081/api/ruleengine/health | jq

# 返回示例:
# {
#   "Status": "healthy",
#   "MonitorCount": 150,
#   "ActiveWorkers": 3,
#   "WorkerDistribution": { "worker-1": 50, "worker-2": 50, "worker-3": 50 }
# }
```

### 查看 SIS 服务日志

```bash
docker compose logs -f --tail=100 sis-service
```

---

## 注意事项

1. **net6.0 兼容性**: Monitor Center 使用 net6.0，SIS 主框架也是 net6.0，与规则引擎的 net10.0 是独立进程
2. **插件格式**: Monitor Center 通过 `Luculent.Sis.MonitorCenter.txt` 文件标识版本 (Git commit hash)
3. **配置热更新**: 修改 `appsettings.json` 后需要重启 sis-service 才能生效
4. **端口占用**: SIS 主服务在 11080，规则引擎 Master 在 11081，确保不冲突
5. **数据库**: SIS 使用 PostgreSQL + MongoDB，规则引擎使用 Redis + ClickHouse，完全独立
