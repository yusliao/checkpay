using System.Text;
using CheckPay.Domain.Entities;

namespace CheckPay.Application.Common;

public sealed record CustomerCsvImportRow(
    int LineNumber,
    string CustomerCode,
    string CustomerName,
    string? ExpectedBankName,
    string? OcrCompanyName,
    string? ExpectedAccountAddress,
    string? CompanyNamesRaw,
    bool? IsActive,
    bool? IsAuthorized);

/// <summary>客户主数据 CSV 导入/导出（UTF-8，与「客户管理」字段一致）。</summary>
public static class CustomerCsvImportExport
{
    public const string Header =
        "客户账号,客户名称,关联银行,票面公司名称,期望地址,关联公司,活跃,已授权";

    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public static byte[] ExportToUtf8BomBytes(IEnumerable<Customer> customers)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Header);
        foreach (var c in customers.OrderBy(x => x.CustomerCode))
            sb.AppendLine(BuildRow(c));
        var body = Utf8NoBom.GetBytes(sb.ToString());
        return Encoding.UTF8.GetPreamble().Concat(body).ToArray();
    }

    public static byte[] TemplateUtf8BomBytes()
    {
        var sb = new StringBuilder();
        sb.AppendLine(Header);
        sb.AppendLine(string.Join(",",
            CsvEscape("示例账号001"),
            CsvEscape("示例客户有限公司"),
            CsvEscape("示例银行"),
            CsvEscape("票面公司名"),
            CsvEscape("地址一行"),
            CsvEscape("关联公司A|关联公司B"),
            "1",
            "0"));
        var body = Utf8NoBom.GetBytes(sb.ToString());
        return Encoding.UTF8.GetPreamble().Concat(body).ToArray();
    }

    private static string BuildRow(Customer c)
    {
        var companies = string.Join("|",
            c.CompanyNames.OrderBy(x => x.CompanyName).Select(x => x.CompanyName));
        var ocr = c.ExpectedCompanyName ?? c.ExpectedAccountHolderName ?? "";
        return string.Join(",",
            CsvEscape(c.CustomerCode),
            CsvEscape(c.CustomerName),
            CsvEscape(c.ExpectedBankName ?? ""),
            CsvEscape(ocr),
            CsvEscape(c.ExpectedAccountAddress ?? ""),
            CsvEscape(companies),
            c.IsActive ? "1" : "0",
            c.IsAuthorized ? "1" : "0");
    }

    public static string CsvEscape(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (s.Contains(',') || s.Contains('"') || s.Contains('\r') || s.Contains('\n'))
            return $"\"{s.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
        return s;
    }

    /// <summary>关联公司单元格：支持 | 分隔；否则按行分割（与表单一致）。</summary>
    public static List<string> ParseCompanyNamesCell(string? cell)
    {
        if (string.IsNullOrWhiteSpace(cell)) return new List<string>();
        var t = cell.Trim();
        if (t.Contains('|', StringComparison.Ordinal))
        {
            return t.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(s => s.Length > 0)
                .GroupBy(CustomerCompanyNameRules.NormalizeKey)
                .Select(g => g.First())
                .ToList();
        }

        return t
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .GroupBy(CustomerCompanyNameRules.NormalizeKey)
            .Select(g => g.First())
            .ToList();
    }

    public static (List<CustomerCsvImportRow> Rows, List<string> Errors) Parse(string utf8Text)
    {
        var errors = new List<string>();
        var rows = new List<CustomerCsvImportRow>();
        if (string.IsNullOrWhiteSpace(utf8Text))
        {
            errors.Add("文件内容为空");
            return (rows, errors);
        }

        if (utf8Text[0] == '\uFEFF')
            utf8Text = utf8Text[1..];

        var allRows = SplitCsvRecords(utf8Text);
        if (allRows.Count == 0)
        {
            errors.Add("无有效数据行");
            return (rows, errors);
        }

        if (!TryMapHeader(allRows[0], out var idx, out var headerError))
        {
            errors.Add(headerError);
            return (rows, errors);
        }

        var seenCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < allRows.Count; i++)
        {
            var lineNo = i + 1;
            var cells = allRows[i];
            if (IsRowEmpty(cells))
                continue;

            string Cell(int col) => col < cells.Length ? cells[col].Trim() : "";

            var code = Cell(idx.Code);
            var name = Cell(idx.Name);
            if (string.IsNullOrWhiteSpace(code) && string.IsNullOrWhiteSpace(name))
                continue;

            if (string.IsNullOrWhiteSpace(code))
            {
                errors.Add($"第 {lineNo} 行：客户账号不能为空");
                continue;
            }

            if (!seenCodes.Add(code))
            {
                errors.Add($"客户账号「{code}」在文件中出现多次");
                continue;
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                errors.Add($"第 {lineNo} 行（{code}）：客户名称不能为空");
                continue;
            }

            var bank = NullIfEmpty(Cell(idx.Bank));
            var ocr = NullIfEmpty(Cell(idx.Ocr));
            var addr = NullIfEmpty(Cell(idx.Addr));
            var companiesCell = Cell(idx.Companies);
            var companies = string.IsNullOrWhiteSpace(companiesCell) ? null : companiesCell.Trim();

            if (!TryParseBoolOptional(Cell(idx.Active), lineNo, "活跃", errors, out var activeOpt))
                continue;
            if (!TryParseBoolOptional(Cell(idx.Auth), lineNo, "已授权", errors, out var authOpt))
                continue;

            rows.Add(new CustomerCsvImportRow(
                lineNo,
                code,
                name.Trim(),
                bank,
                ocr,
                addr,
                companies,
                activeOpt,
                authOpt));
        }

        if (errors.Count > 0)
            return (new List<CustomerCsvImportRow>(), errors);

        return (rows, errors);
    }

    private static bool IsRowEmpty(string[] cells) =>
        cells.Length == 0 || cells.All(c => string.IsNullOrWhiteSpace(c));

    private static string? NullIfEmpty(string s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private readonly struct ColumnIndex(
        int code,
        int name,
        int bank,
        int ocr,
        int addr,
        int companies,
        int active,
        int auth)
    {
        public int Code { get; } = code;
        public int Name { get; } = name;
        public int Bank { get; } = bank;
        public int Ocr { get; } = ocr;
        public int Addr { get; } = addr;
        public int Companies { get; } = companies;
        public int Active { get; } = active;
        public int Auth { get; } = auth;
    }

    private static bool TryMapHeader(string[] headerCells, out ColumnIndex idx, out string error)
    {
        idx = default;
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < headerCells.Length; i++)
        {
            var h = headerCells[i].Trim();
            if (h.Length > 0 && !map.ContainsKey(h))
                map[h] = i;
        }

        int? Find(params string[] names)
        {
            foreach (var n in names)
            {
                if (map.TryGetValue(n, out var i)) return i;
            }
            return null;
        }

        var codeI = Find("客户账号", "CustomerCode");
        var nameI = Find("客户名称", "CustomerName");
        var bankI = Find("关联银行", "ExpectedBankName", "Bank");
        var ocrI = Find("票面公司名称", "OcrCompanyName", "ExpectedCompanyName");
        var addrI = Find("期望地址", "ExpectedAccountAddress", "Address");
        var compI = Find("关联公司", "CompanyNames", "Companies");
        var actI = Find("活跃", "IsActive", "Active");
        var authI = Find("已授权", "IsAuthorized", "Authorized");

        if (codeI is null || nameI is null || bankI is null || ocrI is null || addrI is null
            || compI is null || actI is null || authI is null)
        {
            error =
                "表头须包含列：" + Header + "（可使用英文别名如 CustomerCode, CustomerName 等）。";
            return false;
        }

        idx = new ColumnIndex(
            codeI.Value,
            nameI.Value,
            bankI.Value,
            ocrI.Value,
            addrI.Value,
            compI.Value,
            actI.Value,
            authI.Value);
        error = "";
        return true;
    }

    private static bool TryParseBoolOptional(string? s, int lineNo, string colName, List<string> errors,
        out bool? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(s)) return true;
        s = s.Trim();
        if (s is "1" or "true" or "True" or "是" or "Y" or "y" or "yes" or "Yes")
        {
            result = true;
            return true;
        }

        if (s is "0" or "false" or "False" or "否" or "N" or "n" or "no" or "No")
        {
            result = false;
            return true;
        }

        errors.Add($"第 {lineNo} 行：列「{colName}」取值无效（{s}），请使用 1/0或 是/否");
        return false;
    }

    private static List<string[]> SplitCsvRecords(string text)
    {
        var rows = new List<string[]>();
        var row = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    current.Append(c);
                }
            }
            else
            {
                switch (c)
                {
                    case '"':
                        inQuotes = true;
                        break;
                    case ',':
                        row.Add(current.ToString());
                        current.Clear();
                        break;
                    case '\r':
                        break;
                    case '\n':
                        row.Add(current.ToString());
                        rows.Add(row.ToArray());
                        row = new List<string>();
                        current.Clear();
                        break;
                    default:
                        current.Append(c);
                        break;
                }
            }
        }

        row.Add(current.ToString());
        if (row.Count > 1 || !string.IsNullOrWhiteSpace(row[0]))
            rows.Add(row.ToArray());

        return rows;
    }
}
