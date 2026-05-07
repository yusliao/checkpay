using System.Globalization;
using CheckPay.Domain.Entities;

namespace CheckPay.Application.Common;

/// <summary>美国 ACH 银行可导入 CSV：表头与列序对齐客户 xlsx 模板（6 列，无「行头」列）。</summary>
public static class AchBankImportExportCsv
{
    public const string HeaderLine = "ABA,Account number,Account Type,Name,Detail ID,Amount";

    public static string FormatDataRow(CheckRecord c)
    {
        var accountType = string.IsNullOrWhiteSpace(c.AccountType)
            ? "Checking"
            : c.AccountType.Trim();

        return string.Join(',',
            CsvExcelForcedText(c.RoutingNumber),
            CsvExcelForcedText(c.AccountNumber),
            Csv(accountType),
            Csv(c.AccountHolderName),
            CsvExcelForcedText(c.Customer?.MobilePhone),
            c.CheckAmount.ToString("F2", CultureInfo.InvariantCulture));
    }

    /// <summary>Excel 直接打开 CSV 时避免长数字变为科学计数法：写入公式 <c>="…"</c>。</summary>
    private static string CsvExcelForcedText(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return "\"\"";
        var esc = v.Trim().Replace("\"", "\"\"");
        return $"\"=\"\"{esc}\"\"\"";
    }

    private static string Csv(string? v)
    {
        if (string.IsNullOrEmpty(v)) return "\"\"";
        var esc = v.Replace("\"", "\"\"");
        return $"\"{esc}\"";
    }
}
