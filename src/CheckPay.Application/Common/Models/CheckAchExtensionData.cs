using System.Text.Json;
using CheckPay.Application.Common.Interfaces;

namespace CheckPay.Application.Common.Models;

/// <summary>支票 OCR 的 ACH/MICR 扩展字段，用于 JSON 列存储与训练样本。</summary>
public record CheckAchExtensionData(
    string? RoutingNumber,
    string? AccountNumber,
    string? BankName,
    string? AccountHolderName,
    string? AccountAddress,
    string? AccountType,
    string? PayToOrderOf,
    string? ForMemo,
    string? MicrLineRaw,
    string? CheckNumberMicr,
    string? MicrFieldOrderNote)
{
    public static CheckAchExtensionData FromOcrResult(OcrResultDto r) =>
        new(
            r.RoutingNumber,
            r.AccountNumber,
            r.BankName,
            r.AccountHolderName,
            r.AccountAddress,
            r.AccountType,
            r.PayToOrderOf,
            r.ForMemo,
            r.MicrLineRaw,
            r.CheckNumberMicr,
            r.MicrFieldOrderNote);

    public static string? Serialize(CheckAchExtensionData? d)
    {
        if (d == null) return null;
        return JsonSerializer.Serialize(d);
    }

    public static CheckAchExtensionData? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        return JsonSerializer.Deserialize<CheckAchExtensionData>(json);
    }
}
