# BuildKit：`RUN --mount=type=cache` 需在 BuildKit 下构建（Compose v2 / Docker Desktop 默认启用）；
# 构建阶段
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# 网络偶发 EOF 时提升 NuGet 恢复稳定性；offline 减少证书链在线校验失败
# NuGetAudit=false：避免漏洞元数据拉取（NU1900）及额外访问 api.nuget.org，弱网/防火墙下易 SSL EOF（NU1301）
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
    for i in 1 2 3 4 5 6 7 8; do \
      dotnet restore "CheckPay.Web/CheckPay.Web.csproj" \
        -p:NuGetAudit=false -v:m && break || \
      (echo "dotnet restore (csproj-only) failed (attempt $i), retrying..." && sleep $((i * 2))); \
    done

# 再复制其余源码（.dockerignore 已排除 bin/obj）
COPY src/ ./

# 完整源码到位后必须再次 restore：仅 csproj 阶段的 assets 与完整树不一致，publish --no-restore 仍可能触网并报 NU1301
RUN --mount=type=cache,id=checkpay-nuget,target=/root/.nuget/packages \
    for i in 1 2 3 4 5 6 7 8; do \
      dotnet restore "CheckPay.Web/CheckPay.Web.csproj" \
        -p:NuGetAudit=false -v:m && break || \
      (echo "dotnet restore (full source) failed (attempt $i), retrying..." && sleep $((i * 2))); \
    done

WORKDIR "/src/CheckPay.Web"

# 已通过 restore 写好 assets；与上一步共用同一 NuGet 缓存挂载（挂载目录不落镜像层）
RUN --mount=type=cache,id=checkpay-nuget,target=/root/.nuget/packages \
    rm -f /src/CheckPay.Worker/appsettings.json && \
    for i in 1 2 3 4 5 6 7 8; do \
      dotnet publish "CheckPay.Web.csproj" -c Release -o /app/publish --no-restore \
        -p:NuGetAudit=false -v:m && break || \
      (echo "dotnet publish failed (attempt $i), retrying..." && sleep $((i * 2))); \
    done

# 运行阶段
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# Npgsql 可能探测 Kerberos（libgssapi）；aspnet slim 默认无 krb5，缺库时每次连库刷屏 "Cannot load libgssapi_krb5.so.2"
RUN apt-get update && apt-get install -y --no-install-recommends libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app/publish .

# 暴露端口
EXPOSE 8080

# 启动应用
ENTRYPOINT ["dotnet", "CheckPay.Web.dll"]
