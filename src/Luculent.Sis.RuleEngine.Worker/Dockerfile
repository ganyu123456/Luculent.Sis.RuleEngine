# ===== Worker Native AOT 编译 =====
# Stage 1: 编译为原生二进制
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build

WORKDIR /src

# 先还原依赖（利用 Docker 缓存层）
COPY src/Luculent.Sis.RuleEngine.Shared/Luculent.Sis.RuleEngine.Shared.csproj \
     src/Luculent.Sis.RuleEngine.Shared/
COPY src/Luculent.Sis.RuleEngine.Worker/Luculent.Sis.RuleEngine.Worker.csproj \
     src/Luculent.Sis.RuleEngine.Worker/
RUN dotnet restore src/Luculent.Sis.RuleEngine.Worker/Luculent.Sis.RuleEngine.Worker.csproj \
    -r linux-x64

# 复制源码
COPY src/Luculent.Sis.RuleEngine.Shared/ src/Luculent.Sis.RuleEngine.Shared/
COPY src/Luculent.Sis.RuleEngine.Worker/ src/Luculent.Sis.RuleEngine.Worker/

# 安装 Native AOT 编译依赖（clang）
RUN apt-get update && apt-get install -y clang zlib1g-dev && rm -rf /var/lib/apt/lists/*

# Native AOT 发布
# - PublishAot=true: 编译为原生代码
# - StripSymbols=true: 移除调试符号，减小体积
# - InvariantGlobalization=true: 不依赖 ICU 库
# - OptimizePreference=Speed: 优化吞吐量
RUN dotnet publish src/Luculent.Sis.RuleEngine.Worker/Luculent.Sis.RuleEngine.Worker.csproj \
    -c Release \
    -r linux-x64 \
    -o /app \
    -p:PublishAot=true \
    -p:StripSymbols=true \
    -p:InvariantGlobalization=true \
    -p:OptimizationPreference=Speed \
    --self-contained true

# ===== Stage 2: 最小运行时镜像 =====
# chiseled: 无 shell、无包管理器、无 distro，只有运行时依赖
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled

WORKDIR /app

# 从编译阶段复制原生二进制
COPY --from=build /app/Luculent.Sis.RuleEngine.Worker /app/

# Worker gRPC 端口（Master 连接此端口做健康检查）
EXPOSE 50051

# 健康检查：gRPC 端口可达
HEALTHCHECK --interval=15s --timeout=3s --start-period=5s --retries=3 \
    CMD ["/app/Luculent.Sis.RuleEngine.Worker", "--health-check"] || exit 1

ENTRYPOINT ["/app/Luculent.Sis.RuleEngine.Worker"]
