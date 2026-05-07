using System.Globalization;
using ClosedXML.Excel;
using CheckPay.Domain.Entities;

namespace CheckPay.Application.Common;

/// <summary>与 <see cref="AchBankImportExportCsv"/> 同列的 Excel 导出：预设列宽便于阅览；长数字列按文本写入以免丢前导零。</summary>
public static class AchBankImportExportXlsx
{
    /// <summary>列宽（Excel 字符宽度近似值），按 ABA / 账号 / 账户类型 / 户名 / 餐馆号 / 金额。</summary>
    private static readonly double[] ColumnWidths = [16, 26, 22, 52, 18, 16];

    public static byte[] Build(IEnumerable<CheckRecord> rows)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("ACH");

        string[] headers = ["ABA", "Account number", "Account Type", "Name", "Detail ID", "Amount"];
        for (var i = 0; i < headers.Length; i++)
        {
            var cell = ws.Cell(1, i + 1);
            cell.Value = headers[i];
            cell.Style.Font.Bold = true;
        }

        var rowIndex = 2;
        foreach (var c in rows)
        {
            SetTextCell(ws, rowIndex, 1, c.RoutingNumber?.Trim());
            SetTextCell(ws, rowIndex, 2, c.AccountNumber?.Trim());
            SetTextCell(ws, rowIndex, 3,
                string.IsNullOrWhiteSpace(c.AccountType) ? "Checking" : c.AccountType.Trim());
            SetTextCell(ws, rowIndex, 4, c.AccountHolderName);
            SetTextCell(ws, rowIndex, 5, c.Customer?.MobilePhone?.Trim());

            var amountCell = ws.Cell(rowIndex, 6);
            amountCell.Value = c.CheckAmount;
            amountCell.Style.NumberFormat.Format = "0.00";

            rowIndex++;
        }

        for (var i = 0; i < ColumnWidths.Length; i++)
            ws.Column(i + 1).Width = ColumnWidths[i];

        ws.SheetView.FreezeRows(1);
        if (rowIndex > 2)
            ws.Range(1, 1, rowIndex - 1, headers.Length).SetAutoFilter();

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    private static void SetTextCell(IXLWorksheet ws, int row, int col, string? text)
    {
        var cell = ws.Cell(row, col);
        cell.Style.NumberFormat.Format = "@";
        cell.SetValue(text ?? string.Empty);
    }
}
