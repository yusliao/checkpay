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
    string? MicrFieldOrderNote,
    string? CompanyName = null)
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
            r.MicrFieldOrderNote,
            r.CompanyName);

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

    /// <summary>与训练样本 / 纠偏逻辑一致的 ACH 字段等价比较（空白归一为 null）。</summary>
    public static bool EqualsForTraining(CheckAchExtensionData? a, CheckAchExtensionData? b)
    {
        static string? Norm(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        if (a is null && b is null)
            return true;
        if (a is null || b is null)
            return false;

        return string.Equals(Norm(a.RoutingNumber), Norm(b.RoutingNumber), StringComparison.Ordinal)
            && string.Equals(Norm(a.AccountNumber), Norm(b.AccountNumber), StringComparison.Ordinal)
            && string.Equals(Norm(a.BankName), Norm(b.BankName), StringComparison.OrdinalIgnoreCase)
            && string.Equals(Norm(a.AccountHolderName), Norm(b.AccountHolderName), StringComparison.OrdinalIgnoreCase)
            && string.Equals(Norm(a.AccountAddress), Norm(b.AccountAddress), StringComparison.OrdinalIgnoreCase)
            && string.Equals(Norm(a.AccountType), Norm(b.AccountType), StringComparison.OrdinalIgnoreCase)
            && string.Equals(Norm(a.PayToOrderOf), Norm(b.PayToOrderOf), StringComparison.OrdinalIgnoreCase)
            && string.Equals(Norm(a.CompanyName), Norm(b.CompanyName), StringComparison.OrdinalIgnoreCase)
            && string.Equals(Norm(a.ForMemo), Norm(b.ForMemo), StringComparison.OrdinalIgnoreCase)
            && string.Equals(Norm(a.MicrLineRaw), Norm(b.MicrLineRaw), StringComparison.Ordinal)
            && string.Equals(Norm(a.CheckNumberMicr), Norm(b.CheckNumberMicr), StringComparison.Ordinal)
            && string.Equals(Norm(a.MicrFieldOrderNote), Norm(b.MicrFieldOrderNote), StringComparison.OrdinalIgnoreCase);
    }
}
