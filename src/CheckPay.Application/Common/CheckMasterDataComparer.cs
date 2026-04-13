using CheckPay.Domain.Entities;

namespace CheckPay.Application.Common;

/// <summary>OCR 识别的银行名/持有人 与客户主数据期望字段比对。</summary>
public static class CheckMasterDataComparer
{
    public static bool HasMismatch(Customer customer, string? ocrBankName, string? ocrAccountHolderName)
    {
        static string Norm(string? s) =>
            string.IsNullOrWhiteSpace(s)
                ? ""
                : string.Concat(s.Where(ch => !char.IsWhiteSpace(ch))).ToLowerInvariant();

        var b = Norm(ocrBankName);
        var h = Norm(ocrAccountHolderName);

        if (!string.IsNullOrEmpty(customer.ExpectedBankName) && !string.IsNullOrEmpty(b))
        {
            var eb = Norm(customer.ExpectedBankName);
            if (eb != b && !eb.Contains(b) && !b.Contains(eb))
                return true;
        }

        if (!string.IsNullOrEmpty(customer.ExpectedAccountHolderName) && !string.IsNullOrEmpty(h))
        {
            var eh = Norm(customer.ExpectedAccountHolderName);
            if (eh != h && !eh.Contains(h) && !h.Contains(eh))
                return true;
        }

        return false;
    }
}
