using System.Text.RegularExpressions;
using Azure;
using Azure.AI.DocumentIntelligence;
using Azure.AI.Vision.ImageAnalysis;
using CheckPay.Application.Common;
using CheckPay.Application.Common.Interfaces;
using CheckPay.Application.Common.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CheckPay.Infrastructure.Services;

/// <summary>
/// Azure AI Vision OCR 服务（使用 Read API 提取文字，正则解析支票字段）
/// 注意：Azure AI Vision 是通用 OCR，无支票预生成模型，需自行解析文字；可选主路径融合 DI <c>prebuilt-check.us</c>；当 Vision 未解析出可信金额时可单独再调 DI 用 <c>NumberAmount</c> 兜底；金额二次校验亦使用同一模型（REST 2024-11-30）。
/// </summary>
public class AzureOcrService : IOcrService
{
    private const string BankCheckModelId = "prebuilt-check.us";

    private readonly ImageAnalysisClient _client;
    private readonly DocumentIntelligenceClient _documentIntelligenceClient;
    private readonly IBlobStorageService _blobStorageService;
    private readonly ICheckOcrTemplateResolver _templateResolver;
    private readonly ICheckOcrParsedSampleCorrector _parsedSampleCorrector;
    private readonly ILogger<AzureOcrService> _logger;
    private readonly bool _prebuiltCheckEnrichPrimary;
    private readonly bool _amountFallbackWhenVisionFails;
    private readonly bool _micrBottomBandSecondPassEnabled;
    private readonly double _micrBottomBandMinNormCenterY;

    public AzureOcrService(
        IConfiguration configuration,
        ILogger<AzureOcrService> logger,
        IBlobStorageService blobStorageService,
        ICheckOcrTemplateResolver templateResolver,
        ICheckOcrParsedSampleCorrector parsedSampleCorrector)
    {
        var endpoint = configuration["Azure:DocumentIntelligence:Endpoint"]
            ?? throw new InvalidOperationException("Azure AI Vision Endpoint 未配置");
        var apiKey = configuration["Azure:DocumentIntelligence:ApiKey"]
            ?? throw new InvalidOperationException("Azure AI Vision ApiKey 未配置");

        // 手写金额校验走 Document Intelligence v4（prebuilt-check.us）。纯 Computer Vision 资源的 Key 无法调用 DI，会 401；
        // 可单独配置 DocumentAnalysis*，与 Vision Read 使用不同 Azure 资源。
        var documentAnalysisEndpoint = configuration["Azure:DocumentIntelligence:DocumentAnalysisEndpoint"];
        var documentAnalysisApiKey = configuration["Azure:DocumentIntelligence:DocumentAnalysisApiKey"];
        var documentEndpointUri = new Uri(string.IsNullOrWhiteSpace(documentAnalysisEndpoint) ? endpoint : documentAnalysisEndpoint);
        var documentCredential = new AzureKeyCredential(
            string.IsNullOrWhiteSpace(documentAnalysisApiKey) ? apiKey : documentAnalysisApiKey);

        _client = new ImageAnalysisClient(new Uri(endpoint), new AzureKeyCredential(apiKey));
        _documentIntelligenceClient = new DocumentIntelligenceClient(documentEndpointUri, documentCredential);
        _logger = logger;
        _blobStorageService = blobStorageService;
        _templateResolver = templateResolver;
        _parsedSampleCorrector = parsedSampleCorrector;
        _prebuiltCheckEnrichPrimary = string.Equals(
            configuration["Ocr:PrebuiltCheck:EnrichPrimaryResult"],
            "true",
            StringComparison.OrdinalIgnoreCase);
        _amountFallbackWhenVisionFails = !string.Equals(
            configuration["Ocr:PrebuiltCheck:AmountFallbackWhenVisionFails"],
            "false",
            StringComparison.OrdinalIgnoreCase);
        _micrBottomBandSecondPassEnabled = !string.Equals(
            configuration["Ocr:Micr:BottomBandSecondPassEnabled"],
            "false",
            StringComparison.OrdinalIgnoreCase);
        _micrBottomBandMinNormCenterY = ParseMicrBottomBandThreshold(configuration["Ocr:Micr:BottomBandMinNormCenterY"]);
    }

    public async Task<OcrResultDto> ProcessCheckImageAsync(string imageUrl, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Azure Vision OCR 开始处理支票: {ImageUrl}", imageUrl);

        await using var imageStream = await _blobStorageService.DownloadAsync(imageUrl, cancellationToken);
        using var memoryStream = new MemoryStream();
        await imageStream.CopyToAsync(memoryStream, cancellationToken);
        memoryStream.Position = 0;

        var layout = await AnalyzeReadLayoutFromMemoryAsync(memoryStream, cancellationToken);
        var rawText = layout.FullText;
        _logger.LogInformation("Azure Vision OCR 提取原始文字: {RawText}", rawText);

        var micrHintLinesDefault = CountLinesInRegion(layout, CheckOcrParsingProfile.Default.MicrPriorRegion);
        var routingHint = CheckOcrVisionReadParser.ParseMicrHeuristic(layout, CheckOcrParsingProfile.Default).RoutingNumber;
        var resolution = await _templateResolver.ResolveAsync(routingHint, rawText, cancellationToken);
        var profile = resolution.Profile;

        _logger.LogInformation(
            "Azure Vision OCR 票型解析 TemplateId={TemplateId} TemplateName={TemplateName}",
            resolution.TemplateId,
            resolution.TemplateName);

        var (checkNumber, checkConf) = CheckOcrVisionReadParser.ParseCheckNumber(layout, profile);
        var (amount, amountConf) = CheckOcrVisionReadParser.ParseAmount(layout, profile);
        var (date, dateConf) = CheckOcrVisionReadParser.ParseDate(layout, profile);
        var (parsedBankName, parsedBankConf) = CheckOcrVisionReadParser.ParseBankName(layout, profile);
        var (parsedHolderName, parsedHolderConf) = CheckOcrVisionReadParser.ParseAccountHolderName(layout, profile);
        var (parsedCompanyName, parsedCompanyConf) = CheckOcrVisionReadParser.ParseCompanyName(layout, profile);
        var (parsedAddress, parsedAddressConf) = CheckOcrVisionReadParser.ParseAccountAddress(layout, profile);

        _logger.LogInformation(
            "Azure Vision OCR 解析结果 — 支票号: {CheckNumber}({ConfCN:F2}) | 金额: {Amount}({ConfAmt:F2}) | 日期: {Date}({ConfDate:F2})",
            checkNumber, checkConf, amount, amountConf, date?.ToString("yyyy-MM-dd"), dateConf);

        var micrAppliedLines = CountLinesInRegion(layout, profile.MicrPriorRegion);
        var firstPassMicr = CheckOcrVisionReadParser.ParseMicrHeuristic(layout, profile);
        var micr = firstPassMicr;
        var micrSecondPassApplied = false;
        if (_micrBottomBandSecondPassEnabled
            && (micr.RoutingNumber is not { Length: 9 } || micr.RoutingAbaChecksumValid != true))
        {
            var bottomBandMicr = CheckOcrVisionReadParser.ParseMicrHeuristicBottomBand(layout, _micrBottomBandMinNormCenterY);
            if (bottomBandMicr.RoutingNumber is { Length: 9 } && bottomBandMicr.RoutingAbaChecksumValid == true)
            {
                micr = bottomBandMicr;
                micrSecondPassApplied = true;
                _logger.LogInformation(
                    "MICR 底部条带二次解析命中: Routing {BeforeRouting} -> {AfterRouting}, Mode {BeforeMode} -> {AfterMode}, Account {BeforeAccount} -> {AfterAccount}",
                    firstPassMicr.RoutingNumber ?? "null",
                    micr.RoutingNumber ?? "null",
                    firstPassMicr.RoutingSelectionMode,
                    micr.RoutingSelectionMode,
                    firstPassMicr.AccountNumber ?? "null",
                    micr.AccountNumber ?? "null");
            }
        }
        var (payToLine, payToConf) = CheckOcrVisionReadParser.ParsePayToOrderLine(rawText);

        var iban = CheckOcrEuInstrumentParser.TryFindValidIban(rawText);
        var bic = CheckOcrEuInstrumentParser.TryFindBic(rawText);

        var diFields = PrebuiltCheckStructuredFields.Empty;
        var prebuiltStatus = "skipped";
        string? amountFallbackOutcome = null;

        if (_prebuiltCheckEnrichPrimary)
        {
            try
            {
                memoryStream.Position = 0;
                var operation = await _documentIntelligenceClient.AnalyzeDocumentAsync(
                    WaitUntil.Completed,
                    BankCheckModelId,
                    BinaryData.FromStream(memoryStream),
                    cancellationToken);
                diFields = PrebuiltCheckStructuredExtractor.TryExtract(operation.Value);
                prebuiltStatus = diFields.RoutingNumber != null || diFields.BankName != null ? "used" : "empty_model_fields";
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "prebuilt-check.us 主路径融合失败，继续使用 Vision Read 结果");
                prebuiltStatus = "failed";
            }
        }
        else if (_amountFallbackWhenVisionFails && ShouldInvokeDiAmountFallback(amount, amountConf))
        {
            try
            {
                memoryStream.Position = 0;
                var operation = await _documentIntelligenceClient.AnalyzeDocumentAsync(
                    WaitUntil.Completed,
                    BankCheckModelId,
                    BinaryData.FromStream(memoryStream),
                    cancellationToken);
                var fallbackDi = PrebuiltCheckStructuredExtractor.TryExtract(operation.Value);
                if (fallbackDi.NumberAmount is { } na
                    && na > 0m
                    && fallbackDi.NumberAmountConfidence >= 0.6)
                {
                    amount = na;
                    amountConf = Math.Clamp(fallbackDi.NumberAmountConfidence, 0.55, 0.92);
                    amountFallbackOutcome = "applied";
                    _logger.LogInformation(
                        "prebuilt-check.us 金额兜底: Vision 金额不足信，采用 DI NumberAmount={Amount}（置信度 {Conf:F2}）",
                        amount,
                        amountConf);
                }
                else
                    amountFallbackOutcome = "empty";
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "prebuilt-check.us 金额兜底失败，继续使用 Vision Read 金额");
                amountFallbackOutcome = "failed";
            }
        }

        var amountFallbackDiag = FormatAmountFallbackDiagnostic(
            _amountFallbackWhenVisionFails,
            _prebuiltCheckEnrichPrimary,
            amountFallbackOutcome);

        var mergedCheck = checkNumber;
        var mergedCheckConf = checkConf;
        var mergedAmount = amount;
        var mergedAmountConf = amountConf;
        var mergedDate = date;
        var mergedDateConf = dateConf;
        var mergedRouting = micr.RoutingNumber;
        var mergedRtConf = micr.RoutingConfidence;
        var mergedAcct = micr.AccountNumber;
        var mergedAcConf = micr.AccountConfidence;
        var mergedPayTo = payToLine;
        var mergedPayToConf = payToConf;
        string? bankName = parsedBankName;
        var bankNameConf = parsedBankConf;
        string? accountHolder = parsedHolderName;
        var accountHolderConf = parsedHolderConf;
        string? accountAddress = parsedAddress;
        var accountAddressConf = parsedAddressConf;

        if (_prebuiltCheckEnrichPrimary && prebuiltStatus != "failed")
            MergePrebuiltStructuredFields(
                diFields,
                ref mergedCheck,
                ref mergedCheckConf,
                ref mergedAmount,
                ref mergedAmountConf,
                ref mergedDate,
                ref mergedDateConf,
                ref mergedRouting,
                ref mergedRtConf,
                ref mergedAcct,
                ref mergedAcConf,
                ref mergedPayTo,
                ref mergedPayToConf,
                ref bankName,
                ref bankNameConf,
                ref accountHolder,
                ref accountHolderConf,
                ref accountAddress,
                ref accountAddressConf);

        var companyName = !string.IsNullOrWhiteSpace(parsedCompanyName)
            ? parsedCompanyName.Trim()
            : OcrCheckCustomerFields.MergeHolderCompanyDisplayName(null, accountHolder, mergedPayTo);
        var companyConf = ResolveCompanyNameConfidence(
            parsedCompanyName,
            parsedCompanyConf,
            companyName,
            accountHolder,
            accountHolderConf,
            mergedPayTo,
            mergedPayToConf);

        var diagnostics = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["full_text_chars"] = rawText.Length.ToString(),
            ["read_line_count"] = layout.Lines.Count.ToString(),
            ["micr_hint_default_region_lines"] = micrHintLinesDefault.ToString(),
            ["micr_applied_template_region_lines"] = micrAppliedLines.ToString(),
            ["micr_selection_mode"] = micr.RoutingSelectionMode,
            ["micr_first_pass_selection_mode"] = firstPassMicr.RoutingSelectionMode,
            ["routing_aba_checksum_ok"] = micr.RoutingAbaChecksumValid?.ToString() ?? "n/a",
            ["routing_aba_checksum_ok_first_pass"] = firstPassMicr.RoutingAbaChecksumValid?.ToString() ?? "n/a",
            ["routing_number_first_pass"] = firstPassMicr.RoutingNumber ?? string.Empty,
            ["routing_number_final"] = micr.RoutingNumber ?? string.Empty,
            ["account_number_first_pass"] = firstPassMicr.AccountNumber ?? string.Empty,
            ["account_number_final"] = micr.AccountNumber ?? string.Empty,
            ["tail_digit_run_count"] = CheckOcrVisionReadParser.CountTailDigitRuns(rawText).ToString(),
            ["micr_bottom_band_second_pass_enabled"] = _micrBottomBandSecondPassEnabled.ToString(),
            ["micr_bottom_band_second_pass_applied"] = micrSecondPassApplied.ToString(),
            ["micr_bottom_band_min_norm_center_y"] = _micrBottomBandMinNormCenterY.ToString("F2"),
            ["prebuilt_check_primary"] = prebuiltStatus,
            ["prebuilt_check_amount_fallback"] = amountFallbackDiag,
            ["eu_iban_present"] = (iban != null).ToString(),
            ["eu_bic_present"] = (bic != null).ToString(),
            ["bank_name_source"] = bankName is null ? "none" : (diFields.BankName is not null ? "prebuilt_or_merged" : "vision_region"),
            ["account_holder_source"] = accountHolder is null ? "none" : (diFields.PayerName is not null ? "prebuilt_or_merged" : "vision_region"),
            ["account_address_source"] = accountAddress is null ? "none" : "vision_region",
            ["company_name_source"] = !string.IsNullOrWhiteSpace(parsedCompanyName)
                ? "vision_company_region"
                : (companyName is null ? "none" : "merged_holder_payto")
        };
        if (resolution.TemplateId.HasValue)
            diagnostics["template_id"] = resolution.TemplateId.Value.ToString("D");
        if (!string.IsNullOrWhiteSpace(resolution.TemplateName))
            diagnostics["template_name"] = resolution.TemplateName!;

        if (micrAppliedLines == 0 && rawText.Length > 40)
            diagnostics["suspect_micr_region_miss"] = "true";

        var micrLineRawSource = "none";
        var micrLineRaw = CheckOcrVisionReadParser.TryResolveMicrLineRawFromLayout(layout, mergedRouting);
        if (!string.IsNullOrEmpty(micrLineRaw))
            micrLineRawSource = "layout";
        else
        {
            micrLineRaw = CheckOcrVisionReadParser.TryBuildMicrLineRawFromPlainText(rawText);
            if (!string.IsNullOrEmpty(micrLineRaw))
                micrLineRawSource = "plain";
            else
            {
                micrLineRaw = micr.MicrLineRaw;
                if (!string.IsNullOrEmpty(micrLineRaw))
                    micrLineRawSource = "heuristic";
            }
        }

        diagnostics["micr_line_raw_source"] = micrLineRawSource;

        _logger.LogInformation("CheckOcrDiagnostics {@Diagnostics}", diagnostics);

        var micConf = micr.MicrLineConfidence;
        var conf = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["CheckNumber"] = mergedCheckConf,
            ["CheckNumberMicr"] = micConf,
            ["Amount"] = mergedAmountConf,
            ["Date"] = mergedDateConf,
            ["RoutingNumber"] = mergedRtConf,
            ["AccountNumber"] = mergedAcConf,
            ["BankName"] = bankName != null ? bankNameConf : 0.1,
            ["AccountHolderName"] = accountHolder != null ? accountHolderConf : 0.1,
            ["AccountAddress"] = accountAddress != null ? accountAddressConf : 0.1,
            ["AccountType"] = 0.1,
            ["PayToOrderOf"] = mergedPayToConf,
            ["CompanyName"] = companyConf,
            ["ForMemo"] = 0.1,
            ["MicrLineRaw"] = micConf
        };

        if (iban != null)
            conf["Iban"] = 0.78;
        if (bic != null)
            conf["Bic"] = 0.65;

        var dto = new OcrResultDto(
            CheckNumber: mergedCheck,
            Amount: mergedAmount,
            Date: mergedDate ?? DateTime.UtcNow,
            ConfidenceScores: conf,
            RoutingNumber: mergedRouting,
            AccountNumber: mergedAcct,
            BankName: bankName,
            AccountHolderName: accountHolder,
            AccountAddress: accountAddress,
            PayToOrderOf: mergedPayTo,
            CompanyName: companyName,
            MicrLineRaw: micrLineRaw,
            CheckNumberMicr: _prebuiltCheckEnrichPrimary ? diFields.CheckNumberMicr : null,
            ExtractedText: rawText,
            Iban: iban,
            Bic: bic,
            Diagnostics: diagnostics);

        return await _parsedSampleCorrector.ApplyIfMatchedAsync(dto, cancellationToken);
    }

    private static int CountLinesInRegion(ReadOcrLayout layout, NormRegion? region)
    {
        if (region is null || layout.Lines.Count == 0)
            return 0;
        return layout.Lines.Count(l => region.Contains(l.NormCenterX, l.NormCenterY));
    }

    private static double ParseMicrBottomBandThreshold(string? raw)
    {
        if (!double.TryParse(raw, out var v))
            return 0.78;
        return Math.Clamp(v, 0.6, 0.95);
    }

    /// <summary>Vision 金额是否弱到需要尝试 DI <c>NumberAmount</c> 兜底（与主路径融合中的覆盖阈值对齐）。</summary>
    internal static bool ShouldInvokeDiAmountFallback(decimal visionAmount, double visionAmountConf) =>
        visionAmount <= 0m || visionAmountConf < 0.52;

    private static string FormatAmountFallbackDiagnostic(
        bool fallbackEnabled,
        bool enrichPrimary,
        string? outcome) =>
        !fallbackEnabled ? "off" : enrichPrimary ? "skipped" : outcome ?? "skipped";

    private static double ResolveCompanyNameConfidence(
        string? parsedCompanyName,
        double parsedCompanyConf,
        string? companyName,
        string? accountHolder,
        double accountHolderConf,
        string? mergedPayTo,
        double mergedPayToConf)
    {
        if (!string.IsNullOrWhiteSpace(parsedCompanyName))
            return parsedCompanyConf;

        if (string.IsNullOrWhiteSpace(companyName))
            return 0.1;

        var c = companyName.Trim();
        if (accountHolder != null && string.Equals(c, accountHolder.Trim(), StringComparison.Ordinal))
            return accountHolderConf;
        if (mergedPayTo != null && string.Equals(c, mergedPayTo.Trim(), StringComparison.Ordinal))
            return mergedPayToConf;

        return Math.Clamp(Math.Max(mergedPayToConf, accountHolderConf) * 0.92, 0.12, 0.72);
    }

    private static void MergePrebuiltStructuredFields(
        PrebuiltCheckStructuredFields di,
        ref string checkNumber,
        ref double checkConf,
        ref decimal amount,
        ref double amountConf,
        ref DateTime? date,
        ref double dateConf,
        ref string? routing,
        ref double rtConf,
        ref string? account,
        ref double acConf,
        ref string? payTo,
        ref double payToConf,
        ref string? bankName,
        ref double bankNameConf,
        ref string? accountHolder,
        ref double accountHolderConf,
        ref string? accountAddress,
        ref double accountAddressConf)
    {
        if (di.RoutingNumber is { Length: 9 } dr && AbaRoutingNumberValidator.IsValid(dr))
        {
            var preferDi = di.RoutingConfidence >= 0.55
                           || routing is not { Length: 9 }
                           || !AbaRoutingNumberValidator.IsValid(routing);
            if (preferDi)
            {
                routing = dr;
                rtConf = Math.Clamp(Math.Max(rtConf, di.RoutingConfidence), 0.55, 0.92);
            }
        }

        if (di.AccountNumber is string da && da.Length >= 8)
        {
            if (account is null
                || da.Length > account.Length
                || di.AccountConfidence > acConf + 0.12)
            {
                account = da;
                acConf = Math.Clamp(Math.Max(acConf, di.AccountConfidence), 0.52, 0.9);
            }
        }

        if (di.CheckNumberMicr is string dc && dc.Length is >= 3 and <= 9 && di.CheckNumberConfidence >= 0.62)
        {
            checkNumber = dc;
            checkConf = Math.Clamp(Math.Max(checkConf, di.CheckNumberConfidence), 0.58, 0.9);
        }

        if (!string.IsNullOrWhiteSpace(di.BankName))
        {
            bankName = di.BankName.Trim();
            bankNameConf = Math.Clamp(Math.Max(bankNameConf, 0.72), 0.55, 0.9);
        }

        if (!string.IsNullOrWhiteSpace(di.PayTo) && (string.IsNullOrWhiteSpace(payTo) || payToConf < 0.48))
        {
            payTo = di.PayTo.Trim();
            payToConf = Math.Clamp(Math.Max(payToConf, 0.62), 0.5, 0.88);
        }

        if (!string.IsNullOrWhiteSpace(di.PayerName))
        {
            var payer = di.PayerName.Trim();
            if (!CheckOcrVisionReadParser.ShouldSkipDiPayerNameForAccountHolder(payer, bankName))
            {
                accountHolder = payer;
                accountHolderConf = Math.Clamp(Math.Max(accountHolderConf, 0.68), 0.52, 0.88);
            }
        }

        if (!string.IsNullOrWhiteSpace(di.PayerAddress))
        {
            accountAddress = di.PayerAddress.Trim();
            accountAddressConf = Math.Clamp(Math.Max(accountAddressConf, 0.66), 0.5, 0.86);
        }

        if (di.NumberAmount is { } na && na > 0m && di.NumberAmountConfidence >= 0.6
                                         && (amountConf < 0.52 || Math.Abs(amount - na) > 0.02m))
        {
            amount = na;
            amountConf = Math.Clamp(di.NumberAmountConfidence, 0.55, 0.92);
        }

        if (di.CheckDate is { } cd && di.CheckDateConfidence >= 0.55 && (!date.HasValue || dateConf < 0.52))
        {
            date = cd;
            dateConf = Math.Clamp(di.CheckDateConfidence, 0.52, 0.9);
        }
    }

    private async Task<ReadOcrLayout> GetReadLayoutAsync(string imageUrl, CancellationToken cancellationToken)
    {
        await using var imageStream = await _blobStorageService.DownloadAsync(imageUrl, cancellationToken);
        using var memoryStream = new MemoryStream();
        await imageStream.CopyToAsync(memoryStream, cancellationToken);
        memoryStream.Position = 0;
        return await AnalyzeReadLayoutFromMemoryAsync(memoryStream, cancellationToken);
    }

    private async Task<ReadOcrLayout> AnalyzeReadLayoutFromMemoryAsync(MemoryStream memoryStream, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Azure Vision Read 输入大小: {Size} bytes", memoryStream.Length);
        var analysisResult = await _client.AnalyzeAsync(
            BinaryData.FromStream(memoryStream),
            VisualFeatures.Read,
            cancellationToken: cancellationToken);

        return AzureReadLayoutExtractor.From(analysisResult.Value);
    }

    /// <summary>
    /// 扣款凭证：使用 Read API 抽全文 + 与支票类似的启发式字段解析（训练标注与管理端预览）。
    /// </summary>
    public async Task<DebitOcrResultDto> ProcessDebitImageAsync(string imageUrl, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Azure Vision OCR 开始处理扣款凭证: {ImageUrl}", imageUrl);
        var layout = await GetReadLayoutAsync(imageUrl, cancellationToken);
        var rawText = layout.FullText;
        _logger.LogInformation("Azure Vision OCR 扣款凭证全文: {RawText}", rawText);

        var profile = CheckOcrParsingProfile.Default;
        var (checkStr, checkConf) = CheckOcrVisionReadParser.ParseCheckNumber(layout, profile);
        var checkNumber = string.IsNullOrWhiteSpace(checkStr) ? null : checkStr.Trim();

        var (amountVal, amountConf) = CheckOcrVisionReadParser.ParseAmount(layout, profile);
        decimal? amount = amountVal > 0m ? amountVal : null;
        if (amount is null)
            amountConf = 0.12;

        var (dateVal, dateConf) = CheckOcrVisionReadParser.ParseDate(layout, profile);

        var (bankRef, refConf) = CheckOcrVisionReadParser.ParseBankReference(rawText);

        var scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["CheckNumber"] = checkNumber is null ? 0.12 : checkConf,
            ["Amount"] = amount is null ? 0.12 : amountConf,
            ["Date"] = dateVal is null ? 0.12 : dateConf,
            ["BankReference"] = bankRef is null ? 0.12 : refConf
        };

        return new DebitOcrResultDto(checkNumber, amount, dateVal, bankRef, scores, RawExtractedText: rawText);
    }

    public async Task<AmountValidationResult> ValidateHandwrittenAmountAsync(
        string imageUrl,
        decimal numericAmount,
        CancellationToken cancellationToken = default)
    {
        if (numericAmount <= 0m)
            return new AmountValidationResult(numericAmount, null, null, null, 0.0, "skipped", "数字金额无效，跳过校验");

        using var imageStream = await _blobStorageService.DownloadAsync(imageUrl, cancellationToken);
        using var memoryStream = new MemoryStream();
        await imageStream.CopyToAsync(memoryStream, cancellationToken);
        memoryStream.Position = 0;

        _logger.LogInformation(
            "Document Intelligence 手写金额校验: ModelId={ModelId}, Bytes={Bytes}",
            BankCheckModelId,
            memoryStream.Length);

        Operation<AnalyzeResult> operation = await _documentIntelligenceClient.AnalyzeDocumentAsync(
            WaitUntil.Completed,
            BankCheckModelId,
            BinaryData.FromStream(memoryStream),
            cancellationToken);

        var result = operation.Value;
        var (legalRaw, legalAmount, confidence, reason) = ExtractLegalAmountFromDiV4(result);
        if (legalAmount is null)
        {
            return new AmountValidationResult(
                numericAmount,
                null,
                legalRaw,
                null,
                confidence,
                "failed",
                reason ?? "未识别到可解析的手写金额");
        }

        var consistent = Math.Abs(legalAmount.Value - numericAmount) <= 0.01m;
        return new AmountValidationResult(
            numericAmount,
            legalAmount,
            legalRaw,
            consistent,
            confidence,
            "completed",
            consistent ? "手写金额与数字金额一致" : "手写金额与数字金额不一致");
    }

    private static (string? rawText, decimal? amount, double confidence, string? reason) ExtractLegalAmountFromDiV4(AnalyzeResult result)
    {
        if (result.Documents is null || result.Documents.Count == 0)
            return (null, null, 0.0, "Document Intelligence 未返回文档结构");

        var doc = result.Documents[0];
        // v4 prebuilt-check.us：WordAmount / NumberAmount；旧版 prebuilt-check 字段名仍尝试兼容
        var candidateKeys = new[]
        {
            "WordAmount",
            "AmountInWords",
            "AmountWritten",
            "LegalAmount",
            "Amount",
            "PayToTheOrderOf",
            "NumberAmount"
        };

        foreach (var key in candidateKeys)
        {
            if (!doc.Fields.TryGetValue(key, out var field) || field is null)
                continue;

            var confidence = Convert.ToDouble(field.Confidence ?? 0f);
            if (TryGetAmountFromDiV4Field(field, out var amount, out var raw))
                return (raw, amount, confidence, null);
        }

        var dollarLine = result.Content?
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(x => x.Contains("dollar", StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(dollarLine) && TryParseAmountFromWords(dollarLine, out var parsedDollar))
            return (dollarLine, parsedDollar, 0.35, "从全文 Dollar 行启发式解析");

        if (!string.IsNullOrWhiteSpace(result.Content))
        {
            foreach (var rawLine in result.Content.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!LooksLikeWrittenAmountScanLine(rawLine))
                    continue;
                if (TryParseAmountFromWords(rawLine, out var lineAmt))
                    return (rawLine, lineAmt, 0.32, "从全文金额措辞行启发式解析");
            }
        }

        return (dollarLine, null, 0.1, "未找到可解析的手写金额字段");
    }

    private static bool TryGetAmountFromDiV4Field(DocumentField field, out decimal amount, out string raw)
    {
        raw = field.Content ?? string.Empty;
        amount = 0m;

        if (field.FieldType == DocumentFieldType.Currency && field.ValueCurrency is { } currency)
        {
            amount = Convert.ToDecimal(currency.Amount);
            raw = string.IsNullOrWhiteSpace(field.Content) ? amount.ToString("N2") : field.Content;
            return amount > 0;
        }

        if (field.FieldType == DocumentFieldType.Double && field.ValueDouble is { } dbl)
        {
            amount = Convert.ToDecimal(dbl);
            raw = string.IsNullOrWhiteSpace(field.Content) ? amount.ToString("N2") : field.Content;
            return amount > 0;
        }

        if (field.FieldType == DocumentFieldType.Int64 && field.ValueInt64 is { } int64)
        {
            amount = int64;
            raw = string.IsNullOrWhiteSpace(field.Content) ? amount.ToString("N2") : field.Content;
            return amount > 0;
        }

        if (field.FieldType == DocumentFieldType.String && !string.IsNullOrEmpty(field.ValueString))
        {
            var text = field.ValueString;
            raw = text;
            if (TryParseAmountFromWords(text, out var parsed))
            {
                amount = parsed;
                return true;
            }
        }

        return false;
    }

    /// <summary>DI <see cref="AnalyzeResult.Content"/> 逐行兜底：过滤明显非「英文大写金额」行。</summary>
    private static bool LooksLikeWrittenAmountScanLine(string line)
    {
        if (line.Length > 260 || line.Contains('$', StringComparison.Ordinal))
            return false;

        return Regex.IsMatch(line, @"(?i)\b(hundred|thousand|million)\b");
    }

    private static readonly Dictionary<string, int> NumberWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zero"] = 0,
        ["one"] = 1,
        ["two"] = 2,
        ["three"] = 3,
        ["four"] = 4,
        ["five"] = 5,
        ["six"] = 6,
        ["seven"] = 7,
        ["eight"] = 8,
        ["nine"] = 9,
        ["ten"] = 10,
        ["eleven"] = 11,
        ["twelve"] = 12,
        ["thirteen"] = 13,
        ["fourteen"] = 14,
        ["fifteen"] = 15,
        ["sixteen"] = 16,
        ["seventeen"] = 17,
        ["eighteen"] = 18,
        ["nineteen"] = 19,
        ["twenty"] = 20,
        ["thirty"] = 30,
        ["forty"] = 40,
        ["fifty"] = 50,
        ["sixty"] = 60,
        ["seventy"] = 70,
        ["eighty"] = 80,
        ["ninety"] = 90
    };

    private static readonly Dictionary<string, int> ScaleWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["hundred"] = 100,
        ["thousand"] = 1_000,
        ["million"] = 1_000_000
    };

    internal static bool TryParseAmountFromWords(string? source, out decimal amount)
    {
        amount = 0m;
        if (string.IsNullOrWhiteSpace(source))
            return false;

        var working = source.Trim();
        var lower = working.ToLowerInvariant();

        var cents = 0;
        var ampFrac = Regex.Match(lower, @"&\s*(\d)\.(\d)\s*/\s*00");
        if (ampFrac.Success)
        {
            cents = int.Parse(ampFrac.Groups[1].Value) * 10 + int.Parse(ampFrac.Groups[2].Value);
            working = working.Remove(ampFrac.Index, ampFrac.Length).TrimEnd();
            lower = working.ToLowerInvariant();
        }
        else
        {
            var fracMatch = Regex.Match(lower, @"(\d{1,2})\s*/\s*100");
            if (fracMatch.Success)
            {
                cents = int.Parse(fracMatch.Groups[1].Value);
                working = working.Remove(fracMatch.Index, fracMatch.Length).TrimEnd();
            }
            else
            {
                var trail = Regex.Match(lower, @"(?<=[a-z])\s+(\d{1,2})\s*!*\s*$");
                if (trail.Success)
                {
                    cents = int.Parse(trail.Groups[1].Value);
                    working = working.Remove(trail.Index, trail.Length).TrimEnd();
                }
            }
        }

        var text = working.ToLowerInvariant();
        text = Regex.Replace(text, @"(?i)\bthousandh\b", "thousand");
        text = Regex.Replace(text, @"(?i)\beightysix\b", "eighty six");
        text = Regex.Replace(text, @"(?i)\bninetysix\b", "ninety six");
        text = Regex.Replace(text, @"(?i)\bfortyfive\b", "forty five");
        text = Regex.Replace(text, @"(?i)\btwentyfive\b", "twenty five");
        text = Regex.Replace(text, @"(?i)\bthirtyfive\b", "thirty five");
        text = Regex.Replace(text, @"(?i)\bfiftyfive\b", "fifty five");
        text = Regex.Replace(text, @"(?i)\bsixtyfive\b", "sixty five");
        text = Regex.Replace(text, @"(?i)\bseventyfive\b", "seventy five");
        text = Regex.Replace(text, @"(?i)\beightyfive\b", "eighty five");
        text = Regex.Replace(text, @"(?i)\bninetyfive\b", "ninety five");

        text = Regex.Replace(text, @"[^a-z\s\-]", " ");
        text = Regex.Replace(text, @"\b(and|dollars?|only)\b", " ");
        text = Regex.Replace(text, @"\s+", " ").Trim();
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(t => t.Split('-', StringSplitOptions.RemoveEmptyEntries))
            .ToArray();

        long total = 0;
        long current = 0;
        foreach (var token in tokens)
        {
            if (NumberWords.TryGetValue(token, out var n))
            {
                current += n;
                continue;
            }

            if (!ScaleWords.TryGetValue(token, out var scale))
                continue;

            if (scale == 100)
            {
                if (current == 0)
                    current = 1;
                current *= scale;
            }
            else
            {
                total += current * scale;
                current = 0;
            }
        }

        total += current;
        if (total <= 0 && cents <= 0)
            return false;
        amount = total + cents / 100m;
        return amount > 0m;
    }
}
