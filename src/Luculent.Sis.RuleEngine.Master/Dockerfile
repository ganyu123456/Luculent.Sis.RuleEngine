# ===== Master JIT 编译 =====
# Master 需要动态反射能力，使用标准 JIT 编译
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build

WORKDIR /src

# 先还原依赖
COPY src/Luculent.Sis.RuleEngine.Shared/Luculent.Sis.RuleEngine.Shared.csproj \
     src/Luculent.Sis.RuleEngine.Shared/
COPY src/Luculent.Sis.RuleEngine.Worker/Luculent.Sis.RuleEngine.Worker.csproj \
     src/Luculent.Sis.RuleEngine.Worker/
COPY src/Luculent.Sis.RuleEngine.Master/Luculent.Sis.RuleEngine.Master.csproj \
     src/Luculent.Sis.RuleEngine.Master/
RUN dotnet restore src/Luculent.Sis.RuleEngine.Master/Luculent.Sis.RuleEngine.Master.csproj

# 复制源码
COPY src/Luculent.Sis.RuleEngine.Shared/ src/Luculent.Sis.RuleEngine.Shared/
COPY src/Luculent.Sis.RuleEngine.Worker/ src/Luculent.Sis.RuleEngine.Worker/
COPY src/Luculent.Sis.RuleEngine.Master/ src/Luculent.Sis.RuleEngine.Master/

# 标准 JIT 发布（框架依赖，减小镜像体积）
RUN dotnet publish src/Luculent.Sis.RuleEngine.Master/Luculent.Sis.RuleEngine.Master.csproj \
    -c Release \
    -o /app \
    --self-contained false

# ===== Stage 2: 运行时镜像 =====
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble

WORKDIR /app

# 设置监听端口
ENV ASPNETCORE_URLS=http://0.0.0.0:11081

# 安装 curl（健康检查用）
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*

COPY --from=build /app /app

# Master HTTP API 端口
EXPOSE 11081

# 健康检查
HEALTHCHECK --interval=15s --timeout=3s --start-period=10s --retries=3 \
    CMD curl -f http://localhost:11081/api/ruleengine/health || exit 1

ENTRYPOINT ["dotnet", "Luculent.Sis.RuleEngine.Master.dll"]
