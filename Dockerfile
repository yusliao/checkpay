# 构建阶段
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# 复制所有项目文件
COPY src/ ./

# 恢复依赖
RUN dotnet restore "CheckPay.Web/CheckPay.Web.csproj"

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
