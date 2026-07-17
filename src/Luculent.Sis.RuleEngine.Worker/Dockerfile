# ===== Worker JIT 编译 =====
# 使用 JIT 编译以保证 ClickHouse.Client / StackExchange.Redis 兼容性
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build

WORKDIR /src

# 先还原依赖（利用 Docker 缓存层）
COPY src/Luculent.Sis.RuleEngine.Shared/Luculent.Sis.RuleEngine.Shared.csproj \
     src/Luculent.Sis.RuleEngine.Shared/
COPY src/Luculent.Sis.RuleEngine.TrendDb/Luculent.Sis.RuleEngine.TrendDb.csproj \
     src/Luculent.Sis.RuleEngine.TrendDb/
COPY lib/ lib/
COPY src/Luculent.Sis.RuleEngine.Worker/Luculent.Sis.RuleEngine.Worker.csproj \
     src/Luculent.Sis.RuleEngine.Worker/
RUN dotnet restore src/Luculent.Sis.RuleEngine.Worker/Luculent.Sis.RuleEngine.Worker.csproj

# 复制源码
COPY src/Luculent.Sis.RuleEngine.Shared/ src/Luculent.Sis.RuleEngine.Shared/
COPY src/Luculent.Sis.RuleEngine.TrendDb/ src/Luculent.Sis.RuleEngine.TrendDb/
COPY lib/ lib/
COPY src/Luculent.Sis.RuleEngine.Worker/ src/Luculent.Sis.RuleEngine.Worker/

# 标准 JIT 发布
RUN dotnet publish src/Luculent.Sis.RuleEngine.Worker/Luculent.Sis.RuleEngine.Worker.csproj \
    -c Release \
    -o /app \
    --self-contained false

# ===== Stage 2: 运行时镜像 =====
FROM mcr.microsoft.com/dotnet/runtime:10.0-noble

WORKDIR /app

COPY --from=build /app /app

# Worker 端口（预留 gRPC 扩展）
EXPOSE 50051

# 健康检查：进程存活
HEALTHCHECK --interval=15s --timeout=3s --start-period=10s --retries=3 \
    CMD pgrep -f "Luculent.Sis.RuleEngine.Worker" || exit 1

ENTRYPOINT ["dotnet", "Luculent.Sis.RuleEngine.Worker.dll"]
