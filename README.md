# CheckPay - 收款系统

跨国收款数据采集系统，专为处理美国支票收款和银行扣款对账而设计。

## 技术栈

- **前端**: Blazor Server + MudBlazor
- **后端**: ASP.NET Core 10 + EF Core 10
- **数据库**: PostgreSQL 16
- **OCR**: 腾讯混元视觉模型（hunyuan-vision）为主；可选 Azure Document Intelligence
- **存储**: MinIO（S3 兼容，Docker Compose 默认）
- **部署**: Docker Compose（推荐，应用 + PostgreSQL + MinIO）

## 项目状态

核心流程（支票采集、扣款导入、核查、导出、用户与客户管理、OCR 训练样本等）已在仓库实现。**设计与文档可能滞后，以 `src/` 与 EF 迁移为准。**

### 已完成（摘要）

| 阶段 | 内容 |
|------|------|
| ✅ | Solution、EF Core、`docker-compose`、MinIO、混元 OCR、图片代理、认证（Cookie + BCrypt）、主要 Blazor 页面与 Worker 等 |

## 默认账号

首次运行（种子数据）自动创建，请登录后尽快修改密码：

| 用户名 | 默认密码 | 角色 |
|--------|----------|------|
| admin@checkpay.local | admin123 | 管理员 |
| usfinance@checkpay.local | usfinance123 | 美国财务 |
| cnfinance@checkpay.local | cnfinance123 | 大陆财务 |

（若本地种子使用短邮箱形式，以 `src/CheckPay.Web` 种子逻辑为准。）

## Docker Compose 部署（推荐）

```bash
git clone <repository-url>
cd checkpay
docker compose up -d
```

默认 Web: `http://localhost:8080`；PostgreSQL、MinIO 端口见根目录 [CLAUDE.md](CLAUDE.md) 或 [docker-compose.yml](docker-compose.yml)。

## 本地开发

### 前置要求

- .NET 10 SDK
- PostgreSQL 16
- MinIO（可单独 `docker run` 或复用 Compose 中的 minio 服务）

### 运行步骤

1. 克隆仓库并配置 `src/CheckPay.Web/appsettings.Development.json`（已在 .gitignore 中）：

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Host=localhost;Database=checkpay;Username=postgres;Password=yourpassword"
  },
  "Minio": {
    "Endpoint": "localhost:9000",
    "AccessKey": "minioadmin",
    "SecretKey": "minioadmin",
    "BucketName": "checkpay-files",
    "UseSSL": false
  },
  "Hunyuan": {
    "SecretId": "your-secret-id",
    "SecretKey": "your-secret-key",
    "Region": "ap-guangzhou"
  },
  "Azure": {
    "DocumentIntelligence": {
      "Endpoint": "",
      "ApiKey": ""
    }
  }
}
```

2. 数据库迁移：

```bash
dotnet ef database update --project src/CheckPay.Infrastructure --startup-project src/CheckPay.Web
```

3. 启动 Web（需要 OCR 队列处理时再开 Worker）：

```bash
dotnet run --project src/CheckPay.Web
dotnet run --project src/CheckPay.Worker
```

更多说明见 [CLAUDE.md](CLAUDE.md)。

## 核心业务流程

### 流程一：支票采集（美国财务）
1. 上传支票图片 → MinIO
2. OCR（腾讯混元为主）
3. 复核表单（置信度颜色：绿 ≥85% / 橙 60–85% / 红 <60%）
4. 确认入库 → 状态：待扣款

### 流程二：扣款导入（美国财务）
1. 上传银行扣款扫描件
2. 左右分屏手动录入
3. 按支票号自动匹配支票记录
4. 成功 → 待核查；失败 → 异常列表

### 流程三：核查确认（大陆财务）
1. 待核查列表逐条核对
2. 确认无误 → 已确认
3. 存疑 → 填写原因

## 贡献指南

协作规范见 [AGENTS.md](AGENTS.md)。
