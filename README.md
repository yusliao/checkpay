# CheckPay - 收款系统

跨国收款数据采集系统，专为处理美国支票收款和银行扣款对账而设计。

## 技术栈

- **前端**: Blazor Server + MudBlazor
- **后端**: ASP.NET Core 10 + EF Core 10
- **数据库**: PostgreSQL 16
- **OCR**: Azure 双阶段识别（主链路 Vision Read + 条件触发的 Document Intelligence 金额校验）；配置节沿用 `Azure:DocumentIntelligence`；支票 Worker 将主识别写入 `ocr_results.raw_result`，金额校验写入 `amount_validation_*`
- **存储**: MinIO（S3 兼容，Docker Compose 默认）
- **部署**: Docker Compose（推荐，应用 + PostgreSQL + MinIO）

## 项目状态

核心流程（支票采集、扣款导入、核查、导出、用户与客户管理、OCR 训练样本等）已在仓库实现。**设计与文档可能滞后，以 `src/` 与 EF 迁移为准；约定见 [AGENTS.md](AGENTS.md) 与 `.cursor/rules/sync-docs.mdc`。**

### 已完成（摘要）

| 阶段 | 内容 |
|------|------|
| ✅ | Solution、EF Core、`docker-compose`、MinIO、Azure Vision OCR、图片代理、认证（Cookie + BCrypt）、主要 Blazor 页面与 Web 内嵌 OCR Worker 等 |

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

在 `.env` 中配置 **`AZURE_VISION_ENDPOINT`** 与 **`AZURE_VISION_API_KEY`**（见 [.env.example](.env.example)），否则支票 OCR 任务会失败。金额二次校验开关/阈值由 `OCR_AMOUNT_VALIDATION_*` 控制。默认 Web: `http://localhost:8080`；PostgreSQL、MinIO 端口见根目录 [CLAUDE.md](CLAUDE.md) 或 [docker-compose.yml](docker-compose.yml)。

## 本地开发

### 前置要求

- .NET 10 SDK
- PostgreSQL 16
- MinIO（可单独 `docker run` 或复用 Compose 中的 minio 服务）
- **Azure AI Vision**（Computer Vision）资源的 Endpoint 与 Key，用于支票/扣款 OCR

### 运行步骤

1. 克隆仓库并配置 `src/CheckPay.Web/appsettings.Development.json`（建议敏感信息用 User Secrets，勿提交密钥）：

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
  "Azure": {
    "DocumentIntelligence": {
      "Endpoint": "https://your-resource.cognitiveservices.azure.com/",
      "ApiKey": "your-api-key"
    }
  }
}
```

2. 数据库迁移：

```bash
dotnet ef database update --project src/CheckPay.Infrastructure --startup-project src/CheckPay.Web
```

3. 启动 Web（**支票 OCR 队列在 Web 进程内的 `OcrWorker` HostedService 中运行**，无需为 OCR 单独启动 `CheckPay.Worker` 控制台项目）：

```bash
dotnet run --project src/CheckPay.Web
```

更多说明见 [CLAUDE.md](CLAUDE.md)。

## 核心业务流程

### 流程一：支票采集（美国财务）
1. 上传支票图片 → MinIO
2. OCR（Azure Vision Read 主识别；当金额置信度低于阈值时触发 DI 手写金额校验；复核页/上传页支持“手动校验手写金额”并记录审计）
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
