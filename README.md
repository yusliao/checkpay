# CheckPay - 收款系统

跨国收款数据采集系统，专为处理美国支票收款和银行扣款对账而设计。

## 技术栈

- **前端**: Blazor Server + MudBlazor
- **后端**: ASP.NET Core 10 + EF Core 10
- **数据库**: PostgreSQL 16
- **OCR**: 腾讯混元视觉模型（hunyuan-vision）
- **存储**: Azure Blob Storage
- **部署**: Railway

## 项目状态

### 已完成

| 阶段 | 内容 |
|------|------|
| ✅ P0 | Solution 结构、EF Core 配置、数据库迁移（6 张核心表） |
| ✅ P1 | 支票上传、OCR 识别、复核表单（置信度颜色标记）、状态写入 |
| ✅ P2 | 扫描件上传、左右分屏手动录入、自动匹配支票、异常列表 |
| ✅ P3 | Dashboard、待核查列表、状态流转、存疑标记 |
| ✅ P4 | CSV 导出、客户管理、用户管理、审计日志基础设施 |
| ✅ P5 | Cookie 认证（Razor Page 写 Cookie，解决 Blazor Server 限制） |
| ✅ P6 | Azure Blob Storage 集成（上传 / 下载 / 删除） |
| ✅ P7 | 腾讯混元 OCR 集成（hunyuan-vision，替代 Azure Document Intelligence） |
| ✅ P8 | 认证增强：数据库账号 + BCrypt 密码、密码修改、会话滑动过期 |

### 待完成

| 阶段 | 内容 |
|------|------|
| ⏳ P9 | Railway 部署配置和测试 |

## 默认账号

首次运行自动创建，请登录后尽快修改密码：

| 用户名 | 默认密码 | 角色 |
|--------|----------|------|
| admin | admin123 | 管理员 |
| usfinance | usfinance123 | 美国财务 |
| cnfinance | cnfinance123 | 大陆财务 |

## 本地开发

### 前置要求

- .NET 10 SDK
- PostgreSQL 16

### 运行步骤

1. 克隆仓库

```bash
git clone <repository-url>
cd checkpay
```

2. 创建本地配置文件（已在 .gitignore 中，不会上传）

```bash
# src/CheckPay.Web/appsettings.Development.json
```

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Host=localhost;Database=checkpay;Username=postgres;Password=yourpassword"
  },
  "Hunyuan": {
    "SecretId": "your-secret-id",
    "SecretKey": "your-secret-key",
    "Region": "ap-guangzhou"
  },
  "Azure": {
    "BlobStorage": {
      "ConnectionString": "UseDevelopmentStorage=true",
      "ContainerName": "checkpay-files"
    }
  }
}
```

3. 运行数据库迁移（自动在启动时执行，也可手动运行）

```bash
dotnet ef database update --project src/CheckPay.Infrastructure --startup-project src/CheckPay.Web
```

4. 启动应用

```bash
dotnet run --project src/CheckPay.Web
```

5. 访问 http://localhost:5000，使用默认账号登录

## Railway 部署

1. 推送代码到 GitHub
2. 在 Railway 创建新项目，关联 GitHub 仓库
3. 添加 PostgreSQL 插件
4. 配置环境变量（见下方清单）
5. 部署完成，EF Core 迁移自动执行

### 环境变量

```
DATABASE_URL=postgresql://...           # Railway 自动注入
Hunyuan__SecretId=AKIDxxxxxxx
Hunyuan__SecretKey=xxxxxxx
Hunyuan__Region=ap-guangzhou
Azure__BlobStorage__ConnectionString=DefaultEndpointsProtocol=https;...
Azure__BlobStorage__ContainerName=checkpay-files
```

## 核心业务流程

### 流程一：支票采集（美国财务）
1. 上传支票图片 → Azure Blob Storage
2. OCR 识别（腾讯混元 hunyuan-vision）
3. 复核表单（置信度颜色：绿 ≥85% / 橙 60-85% / 红 <60%）
4. 确认入库 → 状态：待扣款

### 流程二：扣款导入（美国财务）
1. 上传银行扣款扫描件
2. 左右分屏手动录入
3. 自动匹配支票记录（按支票号）
4. 匹配成功 → 状态：待核查；匹配失败 → 异常列表

### 流程三：核查确认（大陆财务）
1. 待核查列表逐条核对
2. 确认无误 → 状态：已确认
3. 发现问题 → 标记存疑并填写原因

## 贡献指南

更多协作规范请阅读 [AGENTS.md](AGENTS.md)，里面详细说明项目结构、构建/测试命令、编码准则和 PR 要求，别瞎整。
