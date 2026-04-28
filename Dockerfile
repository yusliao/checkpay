# 构建阶段
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# 网络偶发 EOF 时提升 NuGet 恢复稳定性
ENV DOTNET_SYSTEM_NET_HTTP_SOCKETSHTTPHANDLER_HTTP2SUPPORT=false \
    NUGET_CERT_REVOCATION_MODE=offline

# 证书链更新（某些网络环境下可避免 TLS 失败）
RUN apt-get update && apt-get install -y --no-install-recommends ca-certificates && update-ca-certificates && rm -rf /var/lib/apt/lists/*

# 复制所有项目文件
COPY src/ ./

# 恢复依赖
RUN for i in 1 2 3 4 5; do \
      dotnet restore "CheckPay.Web/CheckPay.Web.csproj" --disable-parallel && break || \
      (echo "dotnet restore failed (attempt $i), retrying..." && sleep $((i * 3))); \
    done

# 构建和发布
WORKDIR "/src/CheckPay.Web"
RUN rm -f /src/CheckPay.Worker/appsettings.json && \
    dotnet publish "CheckPay.Web.csproj" -c Release -o /app/publish

# 运行阶段
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

# 暴露端口
EXPOSE 8080

# 启动应用
ENTRYPOINT ["dotnet", "CheckPay.Web.dll"]
