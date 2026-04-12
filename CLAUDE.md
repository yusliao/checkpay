# CheckPay - 收款系统

## 变更记录 (Changelog)

- **2026-04-08** - 文档收敛：删除过时「技术栈架构决策」文档；根目录与 README 以 Docker Compose + 现有实现为准；不计划 Railway 验证与 Microsoft Entra SSO（`User.EntraId` / `entra_id` 仅为字段名，非联合登录）
- **2026-03-19 02:00:00** - OCR 训练标注页面上线：新增 OcrTrainingSample 实体、EF 迁移（含 Designer.cs）、/admin/ocr-training 页面（上传图片→自动OCR→对比标注→保存样本），NavMenu 管理员菜单新增入口；修复手写迁移缺少 Designer.cs 导致 EF Core 无法识别迁移的问题
- **2026-03-19 01:00:00** - OCR 预填 bug 修复：CheckUpload.razor 和 CheckReview.razor 移除置信度门控（≥0.60才填），改为始终预填识别值，置信度仅影响边框颜色；CleanJson 重写（支持从乱文本中提取第一个JSON对象）；DebitOcrPrompt 加强约束；GetDoubleConfidence 兼容范围字符串（如"0.40-0.55"）；OCR日志级别 Debug→Information（添加解析结果结构化日志）
- **2026-03-19 00:00:00** - OCR 识别率提升：重写 CheckOcrPrompt（Chain-of-Thought + MICR 行定位）、置信度改为 0.0-1.0 浮点数输出（兼容旧字符串）、模型名配置化（Ocr:Model）、OcrWorker 添加智能重试机制（最多 3 次，指数退避）、新增 AddOcrRetryCount 迁移
- **2026-03-18 23:54:00** - 混元OCR集成完成：修复本地MinIO图片访问问题（Base64方案）、添加图片代理端点、修复所有页面图片显示
- **2026-03-18 19:37:00** - Docker部署成功：解决Windows DNS解析问题，应用+PostgreSQL+MinIO完整运行，数据库迁移和种子数据初始化成功
- **2026-03-18 11:00:00** - 完成Docker部署配置：Dockerfile、docker-compose.yml、部署文档，支持一键部署（应用+PostgreSQL+MinIO）
- **2026-03-18 10:34:00** - 完成MinIO存储迁移：从Azure Blob Storage迁移到MinIO（S3兼容对象存储），支持私有环境部署
- **2026-03-16 10:15:00** - 完成认证系统增强：数据库账号+BCrypt密码、密码修改功能、会话滑动过期
- **2026-03-16 01:50:00** - 完成P5开发：简单Cookie认证（硬编码两个账户：admin/admin123, user/user123）
- **2026-03-16 01:35:00** - 完成P6开发：Azure Document Intelligence OCR集成，支持prebuilt-check模型识别
- **2026-03-16 01:20:00** - 完成P7开发：Azure Blob Storage集成，支持文件上传/下载/删除
- **2026-03-13 18:30:00** - 完成P0-P4开发：数据库迁移、用户管理、并发保护、审计日志
- **2026-03-13 10:45:10** - 重新初始化 AI 上下文，完善项目文档结构
- **2026-03-13 10:36:23** - 更新项目架构文档，新增页面交互设计文档
- **2026-03-12 23:21:18** - 初始化项目架构文档

## 项目愿景

CheckPay 是一个跨国收款数据采集系统，专为处理美国支票收款和银行扣款对账而设计。系统通过 OCR 技术自动识别支票信息，支持扣款记录导入和自动匹配，提供完整的收款核查和异常处理流程。

**核心价值**：
- 自动化支票信息采集，减少手工录入错误
- 智能匹配支票与扣款记录，快速发现异常
- 跨时区协作支持（美国财务 + 大陆财务）
- 完整的审计追踪和状态管理

## 架构总览

本项目采用 .NET 10 全栈不分离架构，使用 Blazor Server 实现前后端一体化开发。

### 技术栈

- **前端**: Blazor Server + MudBlazor（Material Design 组件库）
- **后端**: ASP.NET Core 10 + EF Core 10
- **数据库**: PostgreSQL 16
- **OCR**: 腾讯混元视觉模型（hunyuan-vision，支持Base64图片识别）
- **存储**: MinIO（S3 兼容对象存储，支持私有部署）
- **认证**: Cookie认证 + BCrypt密码哈希（数据库账号）
- **部署**: Docker Compose（应用 + PostgreSQL + MinIO）
- **日志**: Serilog + Seq

### 架构特点

- **前后端不分离**: 一套 C# 代码库，SignalR 内置实时通信
- **私有部署**: Docker Compose 一键拉起应用、PostgreSQL、MinIO
- **异步处理**: 内存 Channel 队列 + Worker Service 处理 OCR 任务
- **乐观并发**: 关键表使用 row_version 防止并发冲突

## 模块结构图

```mermaid
graph TD
    A["CheckPay (根)"] --> B["docs - 设计文档"];
    A --> C["src - 源代码"];

    C --> C1["CheckPay.Web - Blazor UI"];
    C --> C2["CheckPay.Application - 业务逻辑"];
    C --> C3["CheckPay.Domain - 领域模型"];
    C --> C4["CheckPay.Infrastructure - 数据访问"];
    C --> C5["CheckPay.Worker - 后台任务"];
    C --> C6["CheckPay.Tests - 测试"];

    click B "./docs/CLAUDE.md" "查看 docs 模块文档"
```

## 模块索引

| 模块路径 | 职责 | 语言 | 状态 |
|---------|------|------|------|
| `docs/` | 数据库设计、页面交互设计文档（以代码与迁移为准） | 文档 | ✅ 参考用 |
| `src/CheckPay.Web` | Blazor Server UI 页面和组件 | C# | ✅ 已完成 |
| `src/CheckPay.Application` | 业务用例（Commands/Queries）和接口定义 | C# | ✅ 已完成 |
| `src/CheckPay.Domain` | 领域模型、实体、枚举、业务规则 | C# | ✅ 已完成 |
| `src/CheckPay.Infrastructure` | EF Core、仓储实现、Azure 客户端 | C# | ✅ 已完成 |
| `src/CheckPay.Worker` | OCR 异步任务处理 | C# | ✅ 已完成 |
| `src/CheckPay.Tests` | 单元测试和集成测试 | C# | ✅ 已完成 |

## 核心业务流程

### 流程一：支票采集（美国财务）
1. 上传支票图片 → MinIO（S3 兼容）
2. OCR 识别（腾讯混元 hunyuan-vision 为主；可选 Azure Document Intelligence 对比）
3. 复核表单（置信度颜色标记：绿/橙/红）
4. 确认入库 → check_records 表（状态：待扣款）

### 流程二：扣款导入（美国财务）
1. 批量上传银行扣款扫描件
2. 左右分屏手动录入（客户编号、支票号、金额、日期、流水号）
3. 自动匹配支票记录（按支票号）
4. 匹配成功 → 状态更新为"待核查"；匹配失败 → 进入异常列表

### 流程三：核查确认（大陆财务）
1. Dashboard 查看待核查数量
2. 逐条核对支票与扣款信息
3. 确认无误 → 状态更新为"已确认"
4. 发现问题 → 标记"存疑"并填写原因

## 数据库设计要点

- **主键**: 全表使用 UUID v4
- **时区**: 所有时间字段存储 UTC（timestamptz）
- **软删除**: 核心表使用 deleted_at 标记
- **乐观并发**: check_records 和 debit_records 使用 row_version
- **审计日志**: audit_logs 表记录所有关键操作

**核心表**：
- `check_records` - 支票记录
- `debit_records` - 扣款记录
- `ocr_results` - OCR 原始结果
- `customers` - 客户主数据
- `users` - 用户账号
- `audit_logs` - 审计日志

## 运行与开发

### 前置要求
- .NET 10 SDK
- PostgreSQL 16
- MinIO 服务器（本地或私有部署）
- （可选）Azure Document Intelligence，用于 OCR 对比或备用

### Docker Compose 部署（推荐）
```bash
# 克隆仓库
git clone <repository-url>
cd checkpay

# 一键启动所有服务（PostgreSQL + MinIO + Web应用）
docker-compose up -d

# 查看服务状态
docker-compose ps

# 查看应用日志
docker-compose logs -f web

# 访问应用
# Web应用: http://localhost:8080
# MinIO控制台: http://localhost:9001 (minioadmin/minioadmin)
# PostgreSQL: localhost:5433 (admin/admin123)

# 停止所有服务
docker-compose down

# 停止并删除数据卷（清空数据库和文件）
docker-compose down -v
```

**默认账号：**
- 管理员：admin@checkpay.local / admin123
- 美国财务：usfinance@checkpay.local / usfinance123
- 大陆财务：cnfinance@checkpay.local / cnfinance123

**Windows Docker Desktop DNS问题解决方案：**
如果遇到容器间DNS解析失败，docker-compose.yml已配置extra_hosts手动映射，无需额外操作。

### 本地开发（不使用Docker）
```bash
# 启动 MinIO
docker run -d \
  -p 9000:9000 \
  -p 9001:9001 \
  --name minio \
  -e "MINIO_ROOT_USER=minioadmin" \
  -e "MINIO_ROOT_PASSWORD=minioadmin" \
  minio/minio server /data --console-address ":9001"

# 恢复依赖
dotnet restore

# 配置环境变量（appsettings.Development.json）
# - ConnectionStrings__DefaultConnection
# - Minio__Endpoint / Minio__AccessKey / Minio__SecretKey / Minio__BucketName
# - Azure__DocumentIntelligence__Endpoint / Azure__DocumentIntelligence__ApiKey

# 运行数据库迁移
dotnet ef database update --project src/CheckPay.Infrastructure

# 启动应用
dotnet run --project src/CheckPay.Web
```

## 测试策略

待代码库建立后补充：
- 单元测试：领域模型、业务逻辑
- 集成测试：API 端点、数据库操作（Testcontainers）
- E2E 测试：关键业务流程

## 编码规范

- **架构模式**: 垂直切片 + 整洁架构
- **依赖方向**: Web → Application → Domain（Domain 不依赖任何层）
- **命名约定**: PascalCase（类/方法）、camelCase（参数/变量）
- **异步优先**: 所有 I/O 操作使用 async/await
- **错误处理**: Result 模式 + 全局异常中间件
- **日志**: 结构化日志（Serilog），审计事件单独 sink

## AI 使用指引

### 当前项目状态

- **阶段**: 核心功能已落地；文档为辅助，以本仓库代码为准
- **已完成**:
  - ✅ 数据库设计、页面交互设计（参考文档，实现见 `src/`）
  - ✅ P0: Solution结构、EF Core配置、数据库迁移
  - ✅ P1: 支票上传、OCR复核、状态写入（11个Razor页面）
  - ✅ P2: 扫描件上传、左右分屏录入、自动匹配、异常列表
  - ✅ P3: Dashboard、待核查列表、状态流转、存疑标记
  - ✅ P4: CSV导出、客户管理、用户管理、审计日志基础设施
  - ✅ P5: 简单Cookie认证（硬编码账户：admin/admin123, user/user123）
  - ✅ P6: Azure Document Intelligence OCR集成（prebuilt-check模型）
  - ✅ P7: MinIO存储集成（从Azure Blob Storage迁移，支持私有部署）
  - ✅ P8: 认证系统增强（数据库账号+BCrypt、密码修改、会话滑动过期）
  - ✅ P9: Docker部署配置（Dockerfile、docker-compose.yml、完整部署验证）
  - ✅ P10: 腾讯混元OCR集成（Base64图片识别、图片代理端点、全页面图片显示修复）
  - ✅ P11: OCR识别率提升（CheckOcrPrompt CoT重写、浮点置信度、模型名配置化、OcrWorker智能重试）
  - ✅ P12: OCR训练标注页面（/admin/ocr-training，上传→识别→标注→保存样本，积累训练数据）
- **不在计划内**: Microsoft Entra 联合登录（SSO）、Railway 作为目标部署平台的验证与文档化（仓库根目录可见历史 `railway.json`，不作推荐路径）

### 与 AI 协作建议

1. **阅读资料**（与代码交叉核对）
   - 本文档「架构总览」「运行与开发」— 反映当前主推的 Docker Compose 形态
   - `docs/收款系统_数据库设计_V1.0.md` — 与 `src/CheckPay.Infrastructure/Migrations` 对照
   - `docs/收款系统_页面交互设计_V1.0.md` — UI/UX 与交互流程

2. **维护优先级（按需）**
   - 缺陷修复与业务小需求
   - 审计日志查询界面、导出增强等增强项（无强制路线图编号）

3. **代码生成原则**
   - 遵循设计文档中的架构决策
   - 数据库操作必须符合数据库设计规范
   - UI 组件必须符合页面交互设计规范
   - 优先使用 MudBlazor 组件库
   - 所有时间处理使用 UTC

4. **关键注意事项**
   - 支票号唯一性校验（实时）
   - 乐观并发控制（row_version）
   - OCR 置信度颜色规则（绿 ≥0.85 / 橙 0.60-0.85 / 红 <0.60）
   - 跨时区时间显示（前端转换）
   - 审计日志记录（所有状态变更）

### 关键文件

- `docs/收款系统_数据库设计_V1.0.md` - 数据库表结构设计参考（以迁移为准）
- `docs/收款系统_页面交互设计_V1.0.md` - 页面布局与交互流程设计
- `docker-compose.yml` - 本地/私有一体化部署

### 成本与运维提示

- 自建 Docker 环境：主要成本为自有主机或与业务一致的托管方案；MinIO 与 PostgreSQL 随 Compose 自管。
- 使用腾讯混元、可选 Azure Document Intelligence 时，按各云厂商实际计费为准。

## 实施记录

### 2026-03-18 开发会话（晚上）

**完成内容：P10 腾讯混元OCR集成和图片显示修复**

#### 1. 混元OCR本地MinIO图片访问问题修复
**问题**：MinIO图片URL是本地路径（`http://localhost:9000/...`），混元API在腾讯云无法访问

**解决方案**：
- 修改HunyuanOcrService.cs，从MinIO下载图片到内存
- 转换为Base64编码
- 使用Data URI格式传给混元API：`data:image/jpeg;base64,{base64}`

**修改文件**：
- `src/CheckPay.Infrastructure/Services/HunyuanOcrService.cs`

#### 2. 图片代理端点实现
**问题**：浏览器无法直接访问Docker容器内MinIO地址（`http://minio:9000/...`）

**解决方案**：
- 创建ImageProxyController.cs API端点
- 后端从MinIO下载图片并返回给浏览器
- 在Program.cs添加AddControllers()和MapControllers()

**新增文件**：
- `src/CheckPay.Web/Controllers/ImageProxyController.cs`

**修改文件**：
- `src/CheckPay.Web/Program.cs`

#### 3. 全页面图片显示修复
**修改页面**：
- `src/CheckPay.Web/Pages/CheckReview.razor`（复核页面）
- `src/CheckPay.Web/Pages/CheckRecords.razor`（收款记录详情）

**实现方法**：
```csharp
private string GetProxyImageUrl(string? imageUrl)
{
    if (string.IsNullOrEmpty(imageUrl)) return string.Empty;
    return $"/api/ImageProxy?url={Uri.EscapeDataString(imageUrl)}";
}
```

**当前系统状态**：
- ✅ 混元OCR成功识别支票（Base64方案）
- ✅ 所有页面支票图片正常显示（代理端点）
- ✅ Docker Compose完整运行（PostgreSQL + MinIO + Web应用）

**下一步工作**：
1. 生产环境部署测试
2. 性能优化和监控
3. 用户培训和文档完善

---

### 2026-03-16 开发会话（下午）

**完成内容：P8 认证系统增强**

#### 1. 数据库账号迁移（Task #5）
- 添加User.PasswordHash字段到User实体
- 创建AddPasswordHashToUser数据库迁移
- 修改Program.cs添加种子数据逻辑
  - 三个默认用户：admin、usfinance、cnfinance
  - 使用BCrypt.Net-Next 4.1.0哈希密码
- 修改Login.razor使用数据库验证
  - 查询Users表验证EntraId和密码
  - 使用BCrypt.Verify验证密码哈希
  - 添加用户ID到Claims

#### 2. 密码修改功能（Task #6）
- 创建ChangePassword.razor页面（/change-password路由）
  - 验证当前密码
  - 验证新密码长度（最少6位）
  - 验证新密码与确认密码一致
  - 使用BCrypt哈希新密码并保存
- 在NavMenu.razor添加"修改密码"入口

#### 3. 会话超时管理（Task #7）
- 在Program.cs添加SlidingExpiration配置
  - 启用滑动过期机制
  - 用户活跃时自动延长会话

#### 4. Mock服务和测试
- 使用MockOcrService和MockBlobStorageService进行独立测试
- 61个单元测试全部通过

**当前系统状态：**
- ✅ 数据库认证：三个默认账号通过种子数据创建
- ✅ 密码安全：BCrypt哈希存储
- ✅ 密码管理：用户可自行修改密码
- ✅ 会话管理：8小时过期+滑动延长
- ✅ 测试覆盖：61个测试全部通过

**下一步工作（历史记录；多数已在后续迭代完成）：**
1. 审计日志查询界面
2. 数据导出增强（Excel、PDF）

### 2026-03-16 开发会话（上午）

**完成内容：P5 简单Cookie认证 + P6 Azure Document Intelligence OCR集成 + P7 Azure Blob Storage集成**

#### P5: 简单Cookie认证

1. **添加认证服务**
   - 在Program.cs添加Cookie认证配置
   - 配置登录路径/login，登出路径/logout
   - 会话有效期8小时

2. **创建登录页面**
   - 创建Login.razor（/login路由）
   - 硬编码两个账户：admin/admin123（管理员）、user/user123（普通用户）
   - 登录成功后创建ClaimsPrincipal并写入Cookie

3. **创建登出页面**
   - 创建Logout.razor（/logout路由）
   - 清除认证Cookie并重定向到登录页

4. **更新App.razor**
   - 添加CascadingAuthenticationState支持
   - 使用AuthorizeRouteView保护路由
   - 未认证用户自动重定向到登录页

5. **更新MainLayout**
   - 显示当前登录用户名
   - 添加登出按钮

#### P6: Azure Document Intelligence OCR集成

1. **添加Azure.AI.FormRecognizer依赖**
   - 在CheckPay.Infrastructure项目添加Azure.AI.FormRecognizer NuGet包（v4.1.0）
   - 在CheckPay.Tests项目添加Azure.AI.FormRecognizer NuGet包（用于测试）

2. **配置文件更新**
   - 更新appsettings.json，添加Azure:DocumentIntelligence配置节
   - 配置Endpoint和ApiKey参数

3. **实现AzureOcrService**
   - 使用DocumentAnalysisClient调用prebuilt-check模型
   - 实现ProcessCheckImageAsync：识别支票号、金额、日期
   - 提取置信度分数（CheckNumber、Amount、Date）
   - 自动类型转换（string→decimal、string→DateTime）

4. **单元测试更新**
   - 修改AzureOcrServiceTests，使用Mock配置而非真实连接
   - 添加构造函数参数验证测试（Endpoint和ApiKey缺失检查）
   - 所有9个单元测试通过

#### P7: Azure Blob Storage集成

1. **添加Azure.Storage.Blobs依赖**
   - 在CheckPay.Infrastructure项目添加Azure.Storage.Blobs NuGet包（v12.27.0）
   - 在CheckPay.Tests项目添加Azure.Storage.Blobs NuGet包（用于测试）

2. **配置文件更新**
   - 更新appsettings.json，添加Azure:BlobStorage配置节
   - 配置ConnectionString和ContainerName参数
   - 本地开发使用UseDevelopmentStorage=true（Azure Storage Emulator）

3. **实现AzureBlobStorageService**
   - 实现UploadAsync：支持文件上传，自动创建容器，生成唯一文件名（GUID前缀）
   - 实现DownloadAsync：支持通过URL下载文件流
   - 实现DeleteAsync：支持删除指定URL的文件
   - 添加GetContentType方法：根据文件扩展名自动识别MIME类型

4. **单元测试更新**
   - 修改AzureBlobStorageServiceTests，使用Mock配置而非真实连接
   - 添加构造函数参数验证测试（ConnectionString和ContainerName缺失检查）

**当前系统状态：**
- ✅ 简单Cookie认证完整实现（硬编码账户）
- ✅ Azure Document Intelligence OCR服务完整实现
- ✅ 支持prebuilt-check模型识别支票信息
- ✅ Azure Blob Storage服务完整实现
- ✅ 支持上传/下载/删除操作
- ✅ 单元测试：9个测试全部通过

**下一步工作：**
1. P8: 认证增强与 Docker 部署验证

---

### 2026-03-13 开发会话

**完成内容：P0-P4 基础架构和核心功能**

#### 1. 数据库迁移（Task #4）
- 修复CheckRecord和DebitRecord一对一关系配置
- 生成InitialCreate迁移，包含6张表完整结构
- 验证索引、外键、约束配置正确

#### 2. 用户管理页面（Task #5）
- 创建Users.razor（/users路由）
- 实现内联编辑、角色管理、启用/停用
- 邮箱与 entra_id 唯一性校验

#### 3. 并发保护（Task #6）
- 在5个关键页面添加DbUpdateConcurrencyException捕获
- CheckReview、DebitImport、ExceptionList、ReviewList、ReviewDetail
- 并发冲突时友好提示并重新加载数据

#### 4. 审计日志（Task #7）
- 创建IAuditLogService接口和AuditLogService实现
- 在ReviewDetail的确认/存疑操作中记录审计日志
- 使用硬编码系统用户ID（待认证完成后替换）

**当前系统状态：**
- ✅ 数据库迁移：6张表结构完整
- ✅ UI页面：11个Razor页面全部实现
- ✅ 并发控制：关键操作已保护
- ✅ 审计日志：基础设施就绪
- ✅ 单元测试：7个测试全部通过

**下一步工作（历史记录）：** 后续已实现 Cookie 认证、Azure DI、存储迁移至 MinIO、Docker Compose 与混元 OCR 等，见上方变更记录。
