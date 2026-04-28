using System.Text.RegularExpressions;
using Azure;
using Azure.AI.DocumentIntelligence;
using Azure.AI.Vision.ImageAnalysis;
using CheckPay.Application.Common.Interfaces;
using CheckPay.Application.Common.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CheckPay.Infrastructure.Services;

/// <summary>
/// Azure AI Vision OCR 服务（使用 Read API 提取文字，正则解析支票字段）
/// 注意：Azure AI Vision 是通用 OCR，无支票预生成模型，需自行解析文字；可选主路径融合 DI <c>prebuilt-check.us</c>；金额二次校验亦使用同一模型（REST 2024-11-30）。
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

        var micrHintForRouting = layout.ConcatLinesInRegion(CheckOcrParsingProfile.Default.MicrPriorRegion, "\n");
        var micrHintLinesDefault = CountLinesInRegion(layout, CheckOcrParsingProfile.Default.MicrPriorRegion);
        if (string.IsNullOrWhiteSpace(micrHintForRouting))
            micrHintForRouting = rawText;

        var routingHint = CheckOcrVisionReadParser.ParseMicrHeuristic(micrHintForRouting).RoutingNumber;
        var resolution = await _templateResolver.ResolveAsync(routingHint, rawText, cancellationToken);
        var profile = resolution.Profile;

        _logger.LogInformation(
            "Azure Vision OCR 票型解析 TemplateId={TemplateId} TemplateName={TemplateName}",
            resolution.TemplateId,
            resolution.TemplateName);

        var (checkNumber, checkConf) = CheckOcrVisionReadParser.ParseCheckNumber(layout, profile);
        var (amount, amountConf) = CheckOcrVisionReadParser.ParseAmount(layout, profile);
        var (date, dateConf) = CheckOcrVisionReadParser.ParseDate(layout, profile);

        _logger.LogInformation(
            "Azure Vision OCR 解析结果 — 支票号: {CheckNumber}({ConfCN:F2}) | 金额: {Amount}({ConfAmt:F2}) | 日期: {Date}({ConfDate:F2})",
            checkNumber, checkConf, amount, amountConf, date?.ToString("yyyy-MM-dd"), dateConf);

        var micrBlock = layout.ConcatLinesInRegion(profile.MicrPriorRegion, "\n");
        var micrAppliedLines = CountLinesInRegion(layout, profile.MicrPriorRegion);
        var micrParseText = string.IsNullOrWhiteSpace(micrBlock) ? rawText : micrBlock;
        var micr = CheckOcrVisionReadParser.ParseMicrHeuristic(micrParseText);
        var (payToLine, payToConf) = CheckOcrVisionReadParser.ParsePayToOrderLine(rawText);

        var iban = CheckOcrEuInstrumentParser.TryFindValidIban(rawText);
        var bic = CheckOcrEuInstrumentParser.TryFindBic(rawText);

        var diFields = PrebuiltCheckStructuredFields.Empty;
        var prebuiltStatus = "skipped";
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
        string? bankName = null;
        string? accountHolder = null;

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
                ref accountHolder);

        var diagnostics = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["full_text_chars"] = rawText.Length.ToString(),
            ["read_line_count"] = layout.Lines.Count.ToString(),
            ["micr_hint_default_region_lines"] = micrHintLinesDefault.ToString(),
            ["micr_applied_template_region_lines"] = micrAppliedLines.ToString(),
            ["micr_selection_mode"] = micr.RoutingSelectionMode,
            ["routing_aba_checksum_ok"] = micr.RoutingAbaChecksumValid?.ToString() ?? "n/a",
            ["tail_digit_run_count"] = CheckOcrVisionReadParser.CountTailDigitRuns(micrParseText).ToString(),
            ["prebuilt_check_primary"] = prebuiltStatus,
            ["eu_iban_present"] = (iban != null).ToString(),
            ["eu_bic_present"] = (bic != null).ToString()
        };
        if (resolution.TemplateId.HasValue)
            diagnostics["template_id"] = resolution.TemplateId.Value.ToString("D");
        if (!string.IsNullOrWhiteSpace(resolution.TemplateName))
            diagnostics["template_name"] = resolution.TemplateName!;

        if (micrAppliedLines == 0 && rawText.Length > 40)
            diagnostics["suspect_micr_region_miss"] = "true";

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
            ["BankName"] = bankName != null ? 0.72 : 0.1,
            ["AccountHolderName"] = accountHolder != null ? 0.68 : 0.1,
            ["AccountAddress"] = 0.1,
            ["AccountType"] = 0.1,
            ["PayToOrderOf"] = mergedPayToConf,
            ["CompanyName"] = mergedPayToConf,
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
            PayToOrderOf: mergedPayTo,
            CompanyName: mergedPayTo,
            MicrLineRaw: micr.MicrLineRaw,
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
        ref string? accountHolder)
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

        if (di.CheckNumberMicr is string dc && dc.Length is >= 4 and <= 9 && di.CheckNumberConfidence >= 0.62)
        {
            checkNumber = dc;
            checkConf = Math.Clamp(Math.Max(checkConf, di.CheckNumberConfidence), 0.58, 0.9);
        }

        if (!string.IsNullOrWhiteSpace(di.BankName))
            bankName = di.BankName.Trim();

        if (!string.IsNullOrWhiteSpace(di.PayTo) && (string.IsNullOrWhiteSpace(payTo) || payToConf < 0.48))
        {
            payTo = di.PayTo.Trim();
            payToConf = Math.Clamp(Math.Max(payToConf, 0.62), 0.5, 0.88);
        }

        if (!string.IsNullOrWhiteSpace(di.PayerName))
            accountHolder = di.PayerName.Trim();

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

        var line = result.Content?
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(x => x.Contains("dollar", StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(line) && TryParseAmountFromWords(line, out var parsed))
            return (line, parsed, 0.35, "从全文 Dollar 行启发式解析");

        return (line, null, 0.1, "未找到可解析的手写金额字段");
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

        var text = source.ToLowerInvariant();
        var fracMatch = Regex.Match(text, @"(\d{1,2})\s*/\s*100");
        var cents = fracMatch.Success ? int.Parse(fracMatch.Groups[1].Value) : 0;

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
