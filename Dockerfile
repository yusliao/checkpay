# BuildKit：`RUN --mount=type=cache` 需在 BuildKit 下构建（Compose v2 / Docker Desktop 默认启用）；
# 构建阶段
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# 网络偶发 EOF 时提升 NuGet 恢复稳定性（需关闭并行时请临时改 dotnet restore）
ENV DOTNET_SYSTEM_NET_HTTP_SOCKETSHTTPHANDLER_HTTP2SUPPORT=false \
    NUGET_CERT_REVOCATION_MODE=offline

# 证书链更新（某些网络环境下可避免 TLS 失败）
RUN apt-get update && apt-get install -y --no-install-recommends ca-certificates && update-ca-certificates && rm -rf /var/lib/apt/lists/*

# 仅复制项目文件：源代码变更时可命中本层及以下 restore 缓存
COPY src/CheckPay.Domain/CheckPay.Domain.csproj CheckPay.Domain/
COPY src/CheckPay.Application/CheckPay.Application.csproj CheckPay.Application/
COPY src/CheckPay.Infrastructure/CheckPay.Infrastructure.csproj CheckPay.Infrastructure/
COPY src/CheckPay.Worker/CheckPay.Worker.csproj CheckPay.Worker/
COPY src/CheckPay.Web/CheckPay.Web.csproj CheckPay.Web/

# NuGet 包目录挂载到 BuildKit 缓存（跨构建复用，显著缩短 restore）
RUN --mount=type=cache,id=checkpay-nuget,target=/root/.nuget/packages \
    for i in 1 2 3 4 5; do \
      dotnet restore "CheckPay.Web/CheckPay.Web.csproj" && break || \
      (echo "dotnet restore failed (attempt $i), retrying..." && sleep $((i * 3))); \
    done

# 再复制其余源码（.dockerignore 已排除 bin/obj）
COPY src/ ./

WORKDIR "/src/CheckPay.Web"

# 已通过 restore 写好 assets；与上一步共用同一 NuGet 缓存挂载（挂载目录不落镜像层）
RUN --mount=type=cache,id=checkpay-nuget,target=/root/.nuget/packages \
    rm -f /src/CheckPay.Worker/appsettings.json && \
    dotnet publish "CheckPay.Web.csproj" -c Release -o /app/publish --no-restore

# 运行阶段
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

# 暴露端口
EXPOSE 8080

# 启动应用
ENTRYPOINT ["dotnet", "CheckPay.Web.dll"]
