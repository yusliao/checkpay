# CheckPay - 收款系统

跨国收款数据采集系统，专为处理美国支票收款和银行扣款对账而设计。

## 技术栈

- **前端**: Blazor Server + MudBlazor
- **后端**: ASP.NET Core 10 + EF Core 10
- **数据库**: PostgreSQL 16
- **OCR**: Azure Vision Read 主识别；可选 **`Ocr:ImagePreprocess:Enabled`**（`OCR_IMAGE_PREPROCESS_ENABLED`）在 Read / DI 前对支票图做纠倾、亮纸区域裁剪、短边放大（默认关）；可选 `Ocr:PrebuiltCheck:EnrichPrimaryResult=true` 再调 **prebuilt-check.us** 与 Read 全量融合；未开该项时默认 **`Ocr:PrebuiltCheck:AmountFallbackWhenVisionFails`**（`OCR_PREBUILT_CHECK_AMOUNT_FALLBACK_WHEN_VISION_FAILS`）在 Vision 金额弱时再调 DI，仅用 **`NumberAmount`** 兜底；金额低于置信度阈值时触发 DI 手写金额校验；同时对 `BankName` / `AccountHolderName` / `AccountAddress`（`accountAddressPriorRegion` 内多行 **Y 连通聚类** 合并；**INC/LLC** 印刷行地址分降权）/ `CompanyName`（`companyNamePriorRegion` + 区域与上半带并集；再不行则 **`ExtractedText` 行序** 伪几何重选；INC./LLC 等加权；**bank/credit union** 且无法人后缀者不作为公司名/持有人候选；**prebuilt** **PayerName≈BankName** 不覆盖 Vision 持有人；训练纠偏不写付款行当公司名）增加版式区域锚点解析（可由票型 `parsing_profile_json` 覆盖）并融合 prebuilt 字段；`RoutingNumber/MICR` 解析增加“底部区域优先 + OCR易错字符归一化（O/0, I/1 等）”；配置节沿用 `Azure:DocumentIntelligence`；结果写入 `ocr_results.raw_result`（含 **`Diagnostics`** 诊断键：**`di_word_amount_raw` / `di_word_amount_confidence` / `di_word_amount_parsed`** 等为 DI **`prebuilt-check.us`** 书面金额快照，`EnrichPrimary` 或金额兜底调用时有值），金额校验写入 `amount_validation_*`。支票 **提交入库**（非草稿）可按 `Ocr:Training:AutoSampleOnCheckSubmit` 自动写入训练样本表。**批量上传**相同图片字节时，按 **`Ocr:Dedup`**（`OCR_DEDUP_*`）对 **`ocr_results.image_content_sha256`** 命中已成功行可 **复用识别结果、跳过 Vision**；上传队列可勾选 **强制重新识别** 绕过去重。排查见 [docs/支票OCR失败排查.md](docs/支票OCR失败排查.md)
- **存储**: MinIO（S3 兼容，Docker Compose 默认）
- **部署**: Docker Compose（推荐，应用 + PostgreSQL + MinIO）
- **客户主数据**: 支票上传/复核在「客户账号」与 **Routing Number（9 位 ABA）** 可解析时，按 `(customer_code, expected_routing_number)` 查询 **customers**：自动带出**餐馆编号**（`MobilePhone`），并在主数据已维护时补齐 **期望银行名、公司/持有人、地址**、**账户类型（Business Checking / Savings）**、**Pay to 收款方（目录规范全称）** 等（空则填；**同一账号+ABA** 下已填内容不因重复同步被覆盖；**切换账号或 ABA** 后可再从主数据覆盖）。同一账号在不同银行需在客户管理中维护不同 **ABA**；Routing 未识别为 9 位数字时仍匹配「未填 ABA」的存量客户。**客户管理**（`/customers`）支持按 **支票账户**、**ABA**、**餐馆编号** 筛选列表；**导出 CSV** 与当前筛选结果一致。客户管理 **CSV** 导出/模板在表头末尾含可选列 **账户类型、PayTo收款方**（与 **ABA路由号**、**餐馆编号** 等并列；旧表头无后两列时仍兼容导入；**餐馆编号** 列旧名 **手机号** 亦可导入）。路由授权角色为 **销售 + 美国财务 + 管理员**（`Sales,USFinance,Admin`）。

## 项目状态

核心流程（支票采集、扣款导入、核查、导出、用户与客户管理、OCR 训练样本等）已在仓库实现。**设计与文档可能滞后，以 `src/` 与 EF 迁移为准；约定见 [AGENTS.md](AGENTS.md) 与 `.cursor/rules/sync-docs.mdc`。**

### 已完成（摘要）

| 阶段 | 内容 |
|------|------|
| ✅ | Solution、EF Core、`docker-compose`、MinIO、Azure Vision OCR、图片代理、认证（Cookie + BCrypt）、主要 Blazor 页面（收款记录 `/records`：列表/抽屉客户侧编号列为「餐馆编号」；已提交且未 ACH 扣款成功时弹框编辑票面；客户管理 `/customers` 等列表支持数据库分页）与 Web 内嵌 OCR Worker 等 |

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
docker compose build   # BuildKit（默认启用）会使用 Dockerfile 内 NuGet 缓存挂载；仅改源码时一般不重复还原全部包
docker compose up -d
```

若构建阶段出现 **NU1301 / NU1900**（无法访问 `api.nuget.org`），请检查宿主机网络/DNS/代理；镜像构建脚本已对 NuGet 关闭审计并加长重试。仍失败时可在能访问公网的环境构建后 `docker save`/`load` 导入。

**反向代理**：请转发 **`X-Forwarded-For`**、**`X-Forwarded-Proto`**（应用已启用 `ForwardedHeaders`），否则 HTTPS 终止在网关时 Cookie/重定向可能异常。**登录 / 多副本**：登录桥接与认证 Cookie 依赖 DataProtection 密钥；Compose 已对 `web` 挂卷 **`dataprotection_keys`**，多实例自托管时需将 **`DATA_PROTECTION_KEYS_DIRECTORY`** 指向各节点可访问的同一目录。

在 `.env` 中配置 **`AZURE_VISION_ENDPOINT`** 与 **`AZURE_VISION_API_KEY`**（见 [.env.example](.env.example)），否则支票 OCR 任务会失败。手写金额二次校验使用 **Document Intelligence** 美国支票模型 **`prebuilt-check.us`**（v4）；若 Vision 与 DI 不在同一 Azure 资源，需另填 **`AZURE_DOCUMENT_INTELLIGENCE_ENDPOINT`** / **`AZURE_DOCUMENT_INTELLIGENCE_API_KEY`**，否则校验常见 **401**；资源与 SDK 不匹配旧模型名时会出现 **404 ModelNotFound**。金额校验开关/阈值由 `OCR_AMOUNT_VALIDATION_*` 控制。可选 **`OCR_PREBUILT_CHECK_ENRICH_PRIMARY_RESULT=true`**（对应 `Ocr:PrebuiltCheck:EnrichPrimaryResult`）在每张支票上多一次 DI 调用以提升路由/银行名等结构化字段；**`OCR_PREBUILT_CHECK_AMOUNT_FALLBACK_WHEN_VISION_FAILS`**（默认 `true`）在未开全量融合时仅在 Vision 金额不可靠时多调一次 DI 补 `NumberAmount`（与全量融合二选一、不双次调用）；`OCR_MICR_BOTTOM_BAND_SECOND_PASS_ENABLED` / `OCR_MICR_BOTTOM_BAND_MIN_NORM_CENTER_Y` 用于在首轮 ABA 校验失败时启用 MICR 底部条带二次解析；**`OCR_IMAGE_PREPROCESS_ENABLED=false`**（默认）控制支票原图是否先做纠倾/裁剪/放大再送 Vision。详见 [docs/支票OCR失败排查.md](docs/支票OCR失败排查.md) 与 [docs/支票OCR离线评估与预处理.md](docs/支票OCR离线评估与预处理.md)。**`OCR_TRAINING_AUTO_SAMPLE_ON_CHECK_SUBMIT`** / **`OCR_TRAINING_AUTO_SAMPLE_REQUIRE_DIFF`** / **`OCR_TRAINING_AUTO_SAMPLE_DEDUP_BY_OCR_RESULT_ID`** / **`OCR_TRAINING_AUTO_SAMPLE_LOG_VERBOSITY`**（Minimal / Verbose / Off）控制支票入库后自动训练样本；自动样本仅在“低置信字段被人工改正”时写入，减少噪声样本。`OCR_CHECK_AZURE_TRAINING_CORRECTION_MODE` 默认 `Similarity`，默认参数改为“即时生效”：`OCR_CHECK_AZURE_TRAINING_CORRECTION_CLUSTER_MIN_SAMPLES`（簇最小样本数，默认 1）与 `OCR_CHECK_AZURE_TRAINING_CORRECTION_SAMPLE_MIN_AGE_MINUTES`（样本最小年龄，默认 0 分钟）；`OCR_CHECK_AZURE_TRAINING_CORRECTION_REQUIRE_TEMPLATE_MATCH=true` 时仅在同模板/同 RTN 簇内纠偏。可选汇总日志 `OCR_CHECK_AZURE_TRAINING_CORRECTION_SUMMARY_ENABLED=true` 与 `OCR_CHECK_AZURE_TRAINING_CORRECTION_SUMMARY_FLUSH_MINUTES=15`，按周期输出命中率、平均改正字段数与 Top 改正字段。默认 Web: `http://localhost:8080`；PostgreSQL、MinIO 端口见根目录 [CLAUDE.md](CLAUDE.md) 或 [docker-compose.yml](docker-compose.yml)。**剪贴板**：在纯 HTTP 内网访问时，部分浏览器不提供 `navigator.clipboard`；支票上传 / 扣款导入「复制全部」通过 `checkPayCopyText`（`textarea` + `execCommand('copy')`）降级；生产环境仍建议启用 **HTTPS** 以获得最佳兼容与安全性。

## 本地开发

### 前置要求

- .NET 10 SDK
- PostgreSQL 16
- MinIO（可单独 `docker run` 或复用 Compose 中的 minio 服务）
- **Azure AI Vision**（Computer Vision Read）的 Endpoint 与 Key，用于支票/扣款主 OCR；若启用金额二次校验，还需能调用 **Document Intelligence**（同一多服务资源，或另配 `Azure:DocumentIntelligence:DocumentAnalysis*` / Compose 中的 `AZURE_DOCUMENT_INTELLIGENCE_*`）

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
2. OCR（Azure Vision Read 主识别；当金额置信度低于阈值时触发 DI 手写金额校验；复核页/上传页支持“手动校验手写金额”与“人工复核完毕”：当金额不一致仅为大小写误报时可人工确认放行提交，并在备注写入复核标记）
3. 复核表单（置信度颜色：绿 ≥85% / 橙 60–85% / 红 <60%）
4. 确认入库 → 状态：待扣款（去重维度为 **同银行（`RoutingNumber`）+ 支票号**；支票号按 `Trim + 大写` 规范化，且包含软删除历史记录，重复会被拦截并提示）

### 流程二：扣款导入（美国财务）
1. 上传银行扣款扫描件
2. 左右分屏手动录入
3. 按支票号自动匹配支票记录
4. 成功 → 待核查；失败 → 异常列表
5. `ACH 支票导出`（`/reports/ach-us`）支持按收款方筛选：`CHEUNG KONG HOLDING INC` / `MAXWELL TRADING`；行复选 + 表头全选后**批量标记 ACH 扣款成功（Y）**；导出 CSV 表头中客户侧编号列为 **餐馆编号**（字段仍对应 `MobilePhone`）。大陆财务 **ACH 已扣款导出**（`/reports/ach-cn`）CSV 同列亦为 **餐馆编号**。

### 流程三：核查确认（大陆财务）
1. 待核查列表逐条核对
2. 确认无误 → 已确认
3. 存疑 → 填写原因

## 贡献指南

协作规范见 [AGENTS.md](AGENTS.md)。

## OCR 训练统计查看

- 管理员可在「系统管理 → OCR 训练效果看板」查看自动训练效果趋势（`/admin/ocr-training-insights`）。
- 页面展示最近 N 天：自动样本占比、平均改正字段数、字段改正 Top、按天趋势，以及 MICR 底部条带二次通道的触发/命中/ABA 修复统计（含按 RTN/模板分组 Top 视图）。
- 实时运行日志可在应用日志/Seq 搜索：
  - 单次纠偏：`已应用训练样本纠偏(强匹配)`、`已应用训练样本纠偏(相似度)`
  - 周期汇总：`CheckOcrTrainingCorrectionSummary`
