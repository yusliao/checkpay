using System.Globalization;
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
    private readonly CheckImagePreprocessOptions _imagePreprocess;
    private readonly bool _amountRoiSecondPassEnabled;
    private readonly AmountRoiSecondPassMode _amountRoiMode;
    private readonly double _amountRoiVisionConfTrigger;
    private readonly double _amountRoiMaxNormArea;
    private readonly bool _amountRoiPrebuiltOnCrop;

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
        _imagePreprocess = CheckImagePreprocessOptions.FromConfiguration(configuration.GetSection("Ocr:ImagePreprocess"));
        _amountRoiSecondPassEnabled = !string.Equals(
            configuration["Ocr:AmountRoiSecondPass:Enabled"],
            "false",
            StringComparison.OrdinalIgnoreCase);
        _amountRoiMode = CheckOcrAmountArbitration.ParseAmountRoiSecondPassMode(
            configuration["Ocr:AmountRoiSecondPass:Mode"]);
        _amountRoiVisionConfTrigger = ParseAmountRoiVisionConfTrigger(configuration["Ocr:AmountRoiSecondPass:VisionConfTrigger"]);
        _amountRoiMaxNormArea = ParseAmountRoiMaxNormArea(configuration["Ocr:AmountRoiSecondPass:MaxRoiNormArea"]);
        _amountRoiPrebuiltOnCrop = !string.Equals(
            configuration["Ocr:AmountRoiSecondPass:PrebuiltCheckOnCrop"],
            "false",
            StringComparison.OrdinalIgnoreCase);
    }

    public async Task<OcrResultDto> ProcessCheckImageAsync(string imageUrl, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Azure Vision OCR 开始处理支票: {ImageUrl}", imageUrl);

        await using var imageStream = await _blobStorageService.DownloadAsync(imageUrl, cancellationToken);
        using var memoryStream = new MemoryStream();
        await imageStream.CopyToAsync(memoryStream, cancellationToken);
        memoryStream.Position = 0;

        var imagePre = ApplyCheckImagePreprocessIfEnabled(memoryStream);
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
        var (amount, amountConf, amountParseMode) = CheckOcrVisionReadParser.ParseAmount(layout, profile);
        PrebuiltCheckStructuredFields? amountRoiCropDi = null;
        var amountRoiCropPrebuiltDiag = !_amountRoiSecondPassEnabled ? "disabled" : (!_amountRoiPrebuiltOnCrop ? "off" : "skipped");
        var amountRoiSecondPassOutcome = !_amountRoiSecondPassEnabled ? "disabled" : "skipped";
        if (_amountRoiSecondPassEnabled)
        {
            var roi = await TryAmountRoiSecondPassAsync(
                memoryStream,
                layout,
                profile,
                rawText,
                amount,
                amountConf,
                amountParseMode,
                cancellationToken);
            amountRoiSecondPassOutcome = roi.Outcome;
            amount = roi.Amount;
            amountConf = roi.Conf;
            amountParseMode = roi.ParseMode;
            amountRoiCropDi = roi.CropDi;
            amountRoiCropPrebuiltDiag = roi.CropPrebuiltDiag;
        }

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
        var (visionBankFiltered, visionBankConfFiltered) =
            FilterVisionBankMatchingPayee(parsedBankName, parsedBankConf, payToLine);

        var iban = CheckOcrEuInstrumentParser.TryFindValidIban(rawText);
        var bic = CheckOcrEuInstrumentParser.TryFindBic(rawText);

        var diFields = PrebuiltCheckStructuredFields.Empty;
        var diStructuredSnapshot = PrebuiltCheckStructuredFields.Empty;
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
                diStructuredSnapshot = diFields;
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
                diStructuredSnapshot = PrebuiltCheckStructuredExtractor.TryExtract(operation.Value);
                if (diStructuredSnapshot.NumberAmount is { } na
                    && na > 0m
                    && diStructuredSnapshot.NumberAmountConfidence >= 0.6)
                {
                    amount = na;
                    amountConf = Math.Clamp(diStructuredSnapshot.NumberAmountConfidence, 0.55, 0.92);
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
        string? bankName = visionBankFiltered;
        var bankNameConf = visionBankConfFiltered;
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

        var amountWrittenAugApplied =
            ApplyWrittenAmountAugmentationFromVisionText(rawText, ref mergedAmount, ref mergedAmountConf);

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
            ["image_preprocess_config_enabled"] = _imagePreprocess.Enabled.ToString(),
            ["image_preprocess_mode"] = imagePre.Mode,
            ["image_preprocess_pipeline_applied"] = imagePre.PipelineApplied.ToString(),
            ["image_preprocess_skew_detected_deg"] = imagePre.SkewDetectedDeg.ToString("F2", CultureInfo.InvariantCulture),
            ["image_preprocess_skew_applied_deg"] = imagePre.SkewAppliedDeg.ToString("F2", CultureInfo.InvariantCulture),
            ["image_preprocess_content_trim"] = imagePre.ContentTrimApplied.ToString(),
            ["image_preprocess_upscale"] = imagePre.UpscaleApplied.ToString(),
            ["image_preprocess_fallback_reason"] = imagePre.FallbackReason ?? string.Empty,
            ["image_preprocess_skew_applied_bucket"] = SkewAppliedBucket(imagePre.SkewAppliedDeg),
            ["image_preprocess_perspective_correction"] = "skipped_not_implemented_use_deskew_content_trim",
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
            ["amount_roi_second_pass"] = amountRoiSecondPassOutcome,
            ["amount_roi_second_pass_mode"] = _amountRoiMode.ToString(),
            ["amount_roi_crop_prebuilt"] = amountRoiCropPrebuiltDiag,
            ["amount_written_augmentation"] = amountWrittenAugApplied ? "applied" : "skipped",
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

        CheckOcrAmountArbitration.TryApplyEmbeddedCentsRepairWithConfirmation(
            ref mergedAmount,
            ref mergedAmountConf,
            diStructuredSnapshot,
            rawText,
            diagnostics,
            amountRoiCropDi);
        CheckOcrAmountArbitration.TryApplyMultiSourceConsensus(
            ref mergedAmount,
            ref mergedAmountConf,
            diStructuredSnapshot,
            rawText,
            diagnostics,
            amountRoiCropDi);

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
        diagnostics["amount_parse_mode"] = amountParseMode ?? string.Empty;
        diagnostics["di_word_amount_raw"] = diStructuredSnapshot.WordAmountRaw ?? string.Empty;
        diagnostics["di_word_amount_confidence"] =
            diStructuredSnapshot.WordAmountConfidence.ToString("F4", CultureInfo.InvariantCulture);
        diagnostics["di_word_amount_parsed"] =
            diStructuredSnapshot.WordAmountParsed?.ToString("F2", CultureInfo.InvariantCulture) ?? string.Empty;
        if (amountRoiCropDi is not null)
        {
            diagnostics["di_roi_crop_word_amount_raw"] = amountRoiCropDi.WordAmountRaw ?? string.Empty;
            diagnostics["di_roi_crop_word_amount_confidence"] =
                amountRoiCropDi.WordAmountConfidence.ToString("F4", CultureInfo.InvariantCulture);
            diagnostics["di_roi_crop_word_amount_parsed"] =
                amountRoiCropDi.WordAmountParsed?.ToString("F2", CultureInfo.InvariantCulture) ?? string.Empty;
            diagnostics["di_roi_crop_number_amount"] =
                amountRoiCropDi.NumberAmount?.ToString("F2", CultureInfo.InvariantCulture) ?? string.Empty;
            diagnostics["di_roi_crop_number_amount_confidence"] =
                amountRoiCropDi.NumberAmountConfidence.ToString("F4", CultureInfo.InvariantCulture);
        }
        else
        {
            diagnostics["di_roi_crop_word_amount_raw"] = string.Empty;
            diagnostics["di_roi_crop_word_amount_confidence"] = "0.0000";
            diagnostics["di_roi_crop_word_amount_parsed"] = string.Empty;
            diagnostics["di_roi_crop_number_amount"] = string.Empty;
            diagnostics["di_roi_crop_number_amount_confidence"] = "0.0000";
        }

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

    private static string SkewAppliedBucket(double skewAppliedDeg)
    {
        var a = Math.Abs(skewAppliedDeg);
        if (a < 0.5) return "lt0_5deg";
        if (a < 2.0) return "0_5_to_2deg";
        if (a < 6.0) return "2_to_6deg";
        return "6deg_plus";
    }

    private readonly record struct CheckImagePreprocessTrace(
        string Mode,
        bool PipelineApplied,
        double SkewDetectedDeg,
        double SkewAppliedDeg,
        bool ContentTrimApplied,
        bool UpscaleApplied,
        string? FallbackReason);

    /// <summary>将预处理结果写回 <paramref name="memoryStream"/> 供 Vision Read 与 DI 共用；失败回退原字节。</summary>
    private CheckImagePreprocessTrace ApplyCheckImagePreprocessIfEnabled(MemoryStream memoryStream)
    {
        if (!_imagePreprocess.Enabled)
            return new CheckImagePreprocessTrace("off", false, 0, 0, false, false, null);

        var r = CheckImagePreprocessor.TryPreprocessForCheck(memoryStream, _imagePreprocess, _logger);
        try
        {
            if (!r.UsedPreprocessed)
            {
                return new CheckImagePreprocessTrace(
                    r.Mode,
                    false,
                    r.SkewDegreesDetected,
                    r.SkewDegreesApplied,
                    r.ContentTrimApplied,
                    r.MinSideUpscaleApplied,
                    r.FallbackReason);
            }

            memoryStream.SetLength(0);
            r.Stream.Position = 0;
            r.Stream.CopyTo(memoryStream);
            memoryStream.Position = 0;
            return new CheckImagePreprocessTrace(
                r.Mode,
                true,
                r.SkewDegreesDetected,
                r.SkewDegreesApplied,
                r.ContentTrimApplied,
                r.MinSideUpscaleApplied,
                r.FallbackReason);
        }
        finally
        {
            r.Stream.Dispose();
        }
    }

    private readonly record struct AmountRoiSecondPassResult(
        string Outcome,
        decimal Amount,
        double Conf,
        string? ParseMode,
        PrebuiltCheckStructuredFields? CropDi,
        string CropPrebuiltDiag);

    private async Task<AmountRoiSecondPassResult> TryAmountRoiSecondPassAsync(
        MemoryStream memoryStream,
        ReadOcrLayout layout,
        CheckOcrParsingProfile profile,
        string rawText,
        decimal amount,
        double amountConf,
        string? amountParseMode,
        CancellationToken cancellationToken)
    {
        if (!CheckOcrAmountArbitration.ShouldTriggerRoiSecondPass(
                _amountRoiMode,
                _amountRoiVisionConfTrigger,
                amount,
                amountConf,
                layout,
                profile,
                rawText))
            return new AmountRoiSecondPassResult("not_triggered", amount, amountConf, amountParseMode, null, "skipped");

        var roiBounds = CheckOcrAmountRoiRefinement.TryGetAmountRefinementNormRegion(
            layout,
            profile,
            _amountRoiMaxNormArea);
        if (roiBounds is null)
            return new AmountRoiSecondPassResult("no_bounds", amount, amountConf, amountParseMode, null, "skipped");

        memoryStream.Position = 0;
        using var cropped = CheckOcrAmountRoiRefinement.TryCropImageToNormRegion(
            memoryStream,
            roiBounds);
        if (cropped is null)
            return new AmountRoiSecondPassResult("crop_failed", amount, amountConf, amountParseMode, null, "skipped");

        PrebuiltCheckStructuredFields? cropDi = null;
        var cropPrebuiltDiag = !_amountRoiPrebuiltOnCrop ? "off" : "skipped";

        try
        {
            var beforeAmt = amount;
            cropped.Position = 0;
            var analysisResult = await _client.AnalyzeAsync(
                BinaryData.FromStream(cropped),
                VisualFeatures.Read,
                cancellationToken: cancellationToken);
            var roiLayout = AzureReadLayoutExtractor.From(analysisResult.Value);
            var (roiAmt, roiConf, roiMode) = CheckOcrVisionReadParser.ParseAmount(
                roiLayout,
                CheckOcrAmountRoiRefinement.CroppedAmountParseProfile);

            if (_amountRoiPrebuiltOnCrop)
            {
                try
                {
                    cropped.Position = 0;
                    var op = await _documentIntelligenceClient.AnalyzeDocumentAsync(
                        WaitUntil.Completed,
                        BankCheckModelId,
                        BinaryData.FromStream(cropped),
                        cancellationToken);
                    cropDi = PrebuiltCheckStructuredExtractor.TryExtract(op.Value);
                    if (ReferenceEquals(cropDi, PrebuiltCheckStructuredFields.Empty))
                        cropDi = null;
                    var has = cropDi != null
                              && (cropDi.NumberAmount is { } na && na > 0m
                                  || cropDi.WordAmountParsed is { } wp && wp > 0m);
                    cropPrebuiltDiag = has ? "applied" : "empty";
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "金额 ROI 裁剪图 prebuilt-check.us 调用失败");
                    cropPrebuiltDiag = "failed";
                }
            }

            if (roiAmt <= 0m)
                return new AmountRoiSecondPassResult("roi_no_amount", amount, amountConf, amountParseMode, cropDi, cropPrebuiltDiag);

            if (!CheckOcrAmountArbitration.ShouldPreferRoiAmount(beforeAmt, amountConf, roiAmt, roiConf, rawText))
                return new AmountRoiSecondPassResult("roi_kept_primary", amount, amountConf, amountParseMode, cropDi, cropPrebuiltDiag);

            var nextAmount = roiAmt;
            var nextConf = Math.Clamp(Math.Max(roiConf, amountConf * 0.85), 0.22, 0.91);
            var nextMode = (roiMode ?? "parse") + "|amount_roi_read";
            _logger.LogInformation(
                "支票金额 ROI 二次 Vision Read 采纳: {Before} -> {After} (roi_conf {Conf:F2})",
                beforeAmt,
                roiAmt,
                roiConf);
            return new AmountRoiSecondPassResult("applied", nextAmount, nextConf, nextMode, cropDi, cropPrebuiltDiag);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "支票金额 ROI 二次 Vision Read 失败，保留首轮解析");
            return new AmountRoiSecondPassResult("read_failed", amount, amountConf, amountParseMode, cropDi, cropPrebuiltDiag);
        }
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

    private static double ParseAmountRoiVisionConfTrigger(string? raw)
    {
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            return 0.68;
        return Math.Clamp(v, 0.35, 0.95);
    }

    private static double ParseAmountRoiMaxNormArea(string? raw)
    {
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            return 0.55;
        return Math.Clamp(v, 0.20, 0.92);
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

    /// <summary>
    /// Vision Read 左上/中部误把收款人行当银行名时剔除，避免与 Pay to 重复；DI 融合仍可在其后补全银行名。
    /// </summary>
    private static (string? bankName, double conf) FilterVisionBankMatchingPayee(
        string? visionBank,
        double conf,
        string? payTo)
    {
        if (string.IsNullOrWhiteSpace(visionBank) || string.IsNullOrWhiteSpace(payTo))
            return (visionBank, conf);

        static string Norm(string s) => Regex.Replace(s.Trim(), @"\s+", " ").ToLowerInvariant();

        var bn = Norm(visionBank);
        var pt = Norm(payTo);
        if (bn == pt)
            return (null, 0.1);
        const int minSubstring = 8;
        if (bn.Length >= minSubstring && pt.Contains(bn, StringComparison.Ordinal))
            return (null, 0.1);
        if (pt.Length >= minSubstring && bn.Contains(pt, StringComparison.Ordinal))
            return (null, 0.1);

        return (visionBank, conf);
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

        // prebuilt-check.us：数字框常为整数，书面金额含 XX/100 或分列在另一行 — WordAmount 经全文解析可优于 NumberAmount。
        if (di.WordAmountParsed is { } wap && wap > 0m
                                      && di.WordAmountConfidence >= 0.48
                                      && Math.Truncate(wap) == Math.Truncate(amount)
                                      && (amount - Math.Truncate(amount)) < 0.005m
                                      && (wap - Math.Truncate(wap)) >= 0.009m
                                      && Math.Abs(wap - amount) > 0.005m)
        {
            amount = wap;
            amountConf = Math.Clamp(Math.Max(amountConf * 0.92, di.WordAmountConfidence), 0.55, 0.88);
        }

        if (di.CheckDate is { } cd && di.CheckDateConfidence >= 0.55 && (!date.HasValue || dateConf < 0.52))
        {
            date = cd;
            dateConf = Math.Clamp(di.CheckDateConfidence, 0.52, 0.9);
        }
    }

    /// <summary>
    /// Vision 将分列 / percent 小读成「$ … 00」时，用全文英文大写行（取各行解析结果之最大者）补非零分；仅当与大写整数部分与 Vision 一致且 Vision 小数近 0 时采纳。
    /// </summary>
    internal static bool ApplyWrittenAmountAugmentationFromVisionText(
        string rawText,
        ref decimal amount,
        ref double amountConf)
    {
        if (!TryParseBestWrittenAmountFromCheckFullText(rawText, out var written) || written <= 0m)
            return false;

        var intAmt = Math.Truncate(amount);
        var intW = Math.Truncate(written);
        if (intAmt != intW)
            return false;

        var visionFrac = amount - intAmt;
        var writtenFrac = written - intW;
        if (writtenFrac < 0.01m || visionFrac >= 0.005m)
            return false;
        if (Math.Abs(written - amount) < 0.009m)
            return false;

        amount = written;
        amountConf = Math.Clamp(Math.Max(amountConf, 0.64), 0.55, 0.88);
        return true;
    }

    /// <summary>支票全文：不含 $ 的 dollar/千位措辞行里解析金额，取最大值（抗单段噪声）。</summary>
    internal static bool TryParseBestWrittenAmountFromCheckFullText(string rawText, out decimal amount)
    {
        amount = 0m;
        if (string.IsNullOrWhiteSpace(rawText))
            return false;

        decimal best = 0m;
        var found = false;
        foreach (var line in rawText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.Length > 260 || line.Contains('$', StringComparison.Ordinal))
                continue;
            if (!Regex.IsMatch(line, @"(?i)\b(dollars?|hundred|thousand)\b"))
                continue;
            if (!TryParseAmountFromWords(line, out var a, rawText) || a <= 0m)
                continue;
            if (!found || a > best)
            {
                best = a;
                found = true;
            }
        }

        amount = best;
        return found;
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

        var (amountVal, amountConf, _) = CheckOcrVisionReadParser.ParseAmount(layout, profile);
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
        CancellationToken cancellationToken = default,
        string? companionFullTextForLegalAmount = null)
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
        var (legalRaw, legalAmount, confidence, reason) =
            ExtractLegalAmountFromDiV4(result, numericAmount, companionFullTextForLegalAmount);
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

    private static string MergeLegalAmountCompanionText(string? companion, string documentBody)
    {
        var a = companion?.Trim();
        var b = documentBody.Trim();
        if (string.IsNullOrEmpty(a))
            return b;
        if (string.IsNullOrEmpty(b))
            return a;
        return a + "\n" + b;
    }

    private static bool IsLikelyWordAmountDiFieldKey(string key) =>
        key is "WordAmount" or "AmountInWords" or "AmountWritten" or "LegalAmount";

    /// <summary>
    /// prebuilt-check 偶将金额以「美分整数」或与 dollars 连在一起返回（例 1014850 ⇒ 10148.50）。
    /// 仅当除以 100 后的整数美元与票面数字近似、且原值美分部分非零时降级，以免误缩巨大整数。
    /// </summary>
    internal static decimal RescaleSuspectDiMinorUnitsToDollars(decimal parsed, decimal numericHint)
    {
        if (numericHint <= 0 || parsed <= 0)
            return parsed;
        if (Math.Abs(parsed - numericHint) <= 0.03m)
            return parsed;

        var rem = parsed % 100m;
        if (rem == 0m)
            return parsed;

        var dollarPart = decimal.Truncate(parsed / 100m);
        var tolerance = Math.Max(0.85m, numericHint * 0.03m);
        if (Math.Abs(dollarPart - numericHint) <= tolerance && parsed >= numericHint * 15m)
            return parsed / 100m;

        return parsed;
    }

    /// <summary>修复 DI 折断的 «… eight 100 + 换行 DOLLARS»，并按解析 cents 补足 «and XX/100»。</summary>
    internal static string FormatLegalAmountRawForDiagnosticsDisplay(
        string raw,
        decimal parsedAmount,
        string mergedFullText)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return raw;

        if (Regex.IsMatch(raw, @"\d{1,2}\s*/\s*100"))
            return raw;

        var cents = (int)decimal.Round((parsedAmount % 1m) * 100m, 0, MidpointRounding.AwayFromZero);
        if (cents is < 0 or > 99)
            cents = 0;

        if (cents == 0)
        {
            var z = Regex.Replace(raw, @"(?is)\r?\n\s*100\s*\r?\n\s*DOLLARS\s*$", "\nDOLLARS");
            if (z != raw)
                return z.Trim();
            return Regex.Replace(raw, @"(?i)(?<=[a-z])\s+100(\s*\r?\n\s*DOLLARS\s*)$", "$1", RegexOptions.Singleline)
                .Trim();
        }

        var core = Regex.Replace(raw, @"(?is)\s*DOLLARS\s*$", "").Trim();
        core = Regex.Replace(core, @"(?im)\r?\n\s*100\s*$", "").Trim();
        core = Regex.Replace(core, @"(?i)(?<=[a-z])\s+100\s*$", "").Trim();

        return $"{core} and {cents:D2}/100 DOLLARS";
    }

    private static (string? rawText, decimal? amount, double confidence, string? reason) ExtractLegalAmountFromDiV4(
        AnalyzeResult result,
        decimal numericAmountHint,
        string? companionFullTextForLegalAmount)
    {
        if (result.Documents is null || result.Documents.Count == 0)
            return (null, null, 0.0, "Document Intelligence 未返回文档结构");

        var doc = result.Documents[0];
        var documentBody = result.Content ?? string.Empty;
        var mergedFull = MergeLegalAmountCompanionText(companionFullTextForLegalAmount, documentBody);
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
            if (TryGetAmountFromDiV4Field(
                    field,
                    key,
                    out var amount,
                    out var raw,
                    mergedFull,
                    numericAmountHint))
            {
                amount = RefineLegalCentsAgainstVisionSpilledLine(amount, numericAmountHint, mergedFull);
                raw = FormatLegalAmountRawForDiagnosticsDisplay(raw, amount, mergedFull);
                return (raw, amount, confidence, null);
            }
        }

        var dollarLine = mergedFull
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(x => x.Contains("dollar", StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(dollarLine)
            && TryParseAmountFromWords(dollarLine, out var parsedDollar, mergedFull))
        {
            var parsedRef = RefineLegalCentsAgainstVisionSpilledLine(parsedDollar, numericAmountHint, mergedFull);
            return (
                FormatLegalAmountRawForDiagnosticsDisplay(dollarLine, parsedRef, mergedFull),
                parsedRef,
                0.35,
                "从全文 Dollar 行启发式解析");
        }

        if (!string.IsNullOrWhiteSpace(documentBody))
        {
            foreach (var rawLine in documentBody.Split(
                         '\n',
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!LooksLikeWrittenAmountScanLine(rawLine))
                    continue;
                if (TryParseAmountFromWords(rawLine, out var lineAmt, mergedFull))
                {
                    var lineRef = RefineLegalCentsAgainstVisionSpilledLine(lineAmt, numericAmountHint, mergedFull);
                    return (
                        FormatLegalAmountRawForDiagnosticsDisplay(rawLine, lineRef, mergedFull),
                        lineRef,
                        0.32,
                        "从全文金额措辞行启发式解析");
                }
            }
        }

        return (dollarLine, null, 0.1, "未找到可解析的手写金额字段");
    }

    private static bool TryGetAmountFromDiV4Field(
        DocumentField field,
        string fieldKey,
        out decimal amount,
        out string raw,
        string mergedFullTextForFraction,
        decimal numericAmountHint)
    {
        raw = field.Content ?? string.Empty;
        amount = 0m;

        if (field.FieldType == DocumentFieldType.Currency && field.ValueCurrency is { } currency)
        {
            amount = Convert.ToDecimal(currency.Amount);
            raw = string.IsNullOrWhiteSpace(field.Content) ? amount.ToString("N2") : field.Content;
            amount = RescaleSuspectDiMinorUnitsToDollars(amount, numericAmountHint);
            var ok = ApplyWordAmountFallbackIfNumericFarFromVision(
                field,
                fieldKey,
                numericAmountHint,
                ref amount,
                ref raw,
                mergedFullTextForFraction);
            return ok && amount > 0;
        }

        if (field.FieldType == DocumentFieldType.Double && field.ValueDouble is { } dbl)
        {
            amount = Convert.ToDecimal(dbl);
            raw = string.IsNullOrWhiteSpace(field.Content) ? amount.ToString("N2") : field.Content;
            amount = RescaleSuspectDiMinorUnitsToDollars(amount, numericAmountHint);
            var ok = ApplyWordAmountFallbackIfNumericFarFromVision(
                field,
                fieldKey,
                numericAmountHint,
                ref amount,
                ref raw,
                mergedFullTextForFraction);
            return ok && amount > 0;
        }

        if (field.FieldType == DocumentFieldType.Int64 && field.ValueInt64 is { } int64)
        {
            amount = int64;
            raw = string.IsNullOrWhiteSpace(field.Content) ? amount.ToString("N2") : field.Content;
            amount = RescaleSuspectDiMinorUnitsToDollars(amount, numericAmountHint);
            var ok = ApplyWordAmountFallbackIfNumericFarFromVision(
                field,
                fieldKey,
                numericAmountHint,
                ref amount,
                ref raw,
                mergedFullTextForFraction);
            return ok && amount > 0;
        }

        if (field.FieldType == DocumentFieldType.String)
        {
            var text = !string.IsNullOrWhiteSpace(field.ValueString)
                ? field.ValueString
                : (field.Content ?? string.Empty);
            if (string.IsNullOrWhiteSpace(text))
                return false;
            raw = text.Trim();
            if (TryParseAmountFromWords(raw, out var parsed, mergedFullTextForFraction))
            {
                amount = parsed;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 书面金额字段被标成 Currency/double且数值偏离票面过多时，回退解析 <see cref="DocumentField.Content"/> 英文字串。
    /// </summary>
    private static bool ApplyWordAmountFallbackIfNumericFarFromVision(
        DocumentField field,
        string fieldKey,
        decimal numericAmountHint,
        ref decimal amount,
        ref string raw,
        string mergedFullTextForFraction)
    {
        if (!IsLikelyWordAmountDiFieldKey(fieldKey) || numericAmountHint <= 0)
            return true;

        var thresh = Math.Max(2m, numericAmountHint * 0.05m);
        if (Math.Abs(amount - numericAmountHint) <= thresh)
            return true;

        var text = (field.Content ?? field.ValueString ?? string.Empty).Trim();
        if (text.Length < 12 || !Regex.IsMatch(text, @"(?i)\b(hundred|thousand)\b"))
            return true;

        if (!TryParseAmountFromWords(text, out var wordsAmt, mergedFullTextForFraction))
            return false;

        amount = wordsAmt;
        raw = text;
        return true;
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

    /// <summary>支票票面「分列」：**独立行 XX/100** 或 **`$`** 上方的 **单列 1～2 位分**（如 Pay 带宽里误拆出的 <c>54</c>）。</summary>
    private static readonly Regex StandaloneHundredthsOnlyLineRegex = new(
        @"^\s*(\d{1,2})\s*/\s*100(?:%|\s+%|\s+percent|\s+pct)?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex BareDigitsOneOrTwoCentsCandidateLineRegex = new(
        @"^\d{1,2}$",
        RegexOptions.Compiled);

    /// <summary>
    /// 当英文金额行内未含 <c>xx/100</c> 时，从全文拾取「仅分列」行（多行时取首个出现在含 <c>$</c> 数字行之后的候选；若无则试行内 <c>$</c> 上方的紧邻裸分数字）。
    /// </summary>
    private static bool TryPickStandaloneHundredthsFromFullText(string fullText, out int cents)
    {
        cents = 0;
        if (string.IsNullOrWhiteSpace(fullText))
            return false;

        var lines = fullText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        static int LocateFirstPrintDollarLineIndex(string[] L)
        {
            for (var i = 0; i < L.Length; i++)
            {
                if (!L[i].Contains('$', StringComparison.Ordinal) || !Regex.IsMatch(L[i], @"\d"))
                    continue;
                return i;
            }

            return -1;
        }

        var fracHits = new List<(int idx, int c)>();
        for (var i = 0; i < lines.Length; i++)
        {
            var t = lines[i].Trim();
            if (t.Contains('⑆', StringComparison.Ordinal) || t.Contains('⑈', StringComparison.Ordinal))
                continue;
            var m = StandaloneHundredthsOnlyLineRegex.Match(t);
            if (m.Success
                && int.TryParse(
                    m.Groups[1].Value,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var v)
                && v is >= 0 and <= 99)
                fracHits.Add((i, v));
        }

        var dollarIdx = LocateFirstPrintDollarLineIndex(lines);

        if (fracHits.Count > 1)
        {
            if (dollarIdx < 0)
                return false;

            var afterBox = fracHits.Where(h => h.idx > dollarIdx).OrderBy(h => h.idx).ToList();
            if (afterBox.Count == 0)
                return TryPickBareCentsDigitsAbovePrimaryDollarLine(lines, dollarIdx, out cents);

            cents = afterBox[0].c;
            return true;
        }

        if (fracHits.Count == 1)
        {
            cents = fracHits[0].c;
            return true;
        }

        // 无 NN/100：试一试「分列裸分」（例：PAY 带宽里单独一行 <c>54</c> 紧贴 <c>$ 10148 00</c> 上方）
        if (dollarIdx >= 0 && TryPickBareCentsDigitsAbovePrimaryDollarLine(lines, dollarIdx, out cents))
            return true;

        return false;
    }

    /// <summary>首处含金额符号的 $<c>...</c>$ 行之上的「裸 1～2 位数字」分列分；需与同段 **PAY / TO THE / ORDER OF** 等词同带。</summary>
    private static bool TryPickBareCentsDigitsAbovePrimaryDollarLine(
        string[] lines,
        int dollarLineIdx,
        out int cents)
    {
        cents = 0;
        const int AboveWindow = 12;
        var start = Math.Max(0, dollarLineIdx - AboveWindow);

        var candidates = new List<(int idx, int centsValue, bool twoDigits)>();
        for (var i = start; i < dollarLineIdx; i++)
        {
            var t = lines[i].Trim();
            if (t.Contains('⑆', StringComparison.Ordinal)
                || t.Contains('⑈', StringComparison.Ordinal)
                || t.Contains('$', StringComparison.Ordinal))
                continue;

            if (!BareDigitsOneOrTwoCentsCandidateLineRegex.IsMatch(t))
                continue;

            if (!int.TryParse(
                    t,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var cc)
                || cc is < 1 or > 99)
                continue;

            if (!SpansPayOrderLegalBandIncluding(lines, candidateLineIndex: i, dollarLineIdx))
                continue;

            candidates.Add((i, cc, cc >= 10));
        }

        if (candidates.Count == 0)
            return false;

        // 靠 $ 更近优先；两位数（54）优先于单行 5
        candidates.Sort(static (a, b) =>
        {
            var cmp = -a.twoDigits.CompareTo(b.twoDigits);
            if (cmp != 0)
                return cmp;
            return -a.idx.CompareTo(b.idx);
        });

        cents = candidates[0].centsValue;
        return true;
    }

    private static bool SpansPayOrderLegalBandIncluding(string[] lines, int candidateLineIndex, int dollarLineIdx)
    {
        var lo = Math.Max(0, candidateLineIndex - 8);
        for (var j = lo; j <= dollarLineIdx; j++)
            if (LineHintsPrintedLegalAmountProximity(lines[j]))
                return true;
        return false;
    }

    internal static decimal RefineLegalCentsAgainstVisionSpilledLine(
        decimal parsed,
        decimal numericAmountHint,
        string mergedFull)
    {
        if (string.IsNullOrWhiteSpace(mergedFull) || numericAmountHint <= 1m)
            return parsed;

        var wholeParsed = decimal.Truncate(parsed + 0.00000005m);
        var wholeHint = decimal.Truncate(numericAmountHint + 0.00000005m);

        // 票面数字常为「元」对齐；不写死容差过小以免 10147/10148 OCR 漂移
        if (Math.Abs(wholeParsed - wholeHint) > 2m)
            return parsed;

        if (!TryPickStandaloneHundredthsFromFullText(mergedFull, out var picked) || picked is < 1 or > 99)
            return parsed;

        var centsFromParsed = decimal.ToInt32(
            decimal.Round((parsed % 1m) * 100m, 0, MidpointRounding.AwayFromZero));

        return centsFromParsed == picked ? parsed : wholeParsed + picked / 100m;
    }

    private static bool LineHintsPrintedLegalAmountProximity(string line) =>
        Regex.IsMatch(line, @"(?i)\b(order of|pay to|to the|pay\b)\b|\bT\.?O\.?\b");


    internal static bool TryParseAmountFromWords(
        string? source,
        out decimal amount,
        string? fullTextForStandaloneFraction = null)
    {
        amount = 0m;
        if (string.IsNullOrWhiteSpace(source))
            return false;

        var working = source.Trim();
        working = working.Replace('／', '/').Replace('∕', '/').Replace('％', '%');

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
            var fracMatch = Regex.Match(lower, @"(\d{1,2})\s*/\s*100(?:%|\s+%|\s+percent\b|\s+pct\b)?");
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
        text = Regex.Replace(text, @"(?i)\byourty\b", "forty");
        text = Regex.Replace(text, @"(?i)\bfourty\b", "forty");
        text = Regex.Replace(text, @"(?i)\bpor\b", "for");
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

        if (cents == 0 && !string.IsNullOrWhiteSpace(fullTextForStandaloneFraction)
                         && TryPickStandaloneHundredthsFromFullText(fullTextForStandaloneFraction, out var pickedCents))
            cents = pickedCents;

        amount = total + cents / 100m;
        return amount > 0m;
    }
}
