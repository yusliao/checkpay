using CheckPay.Domain.Entities;

namespace CheckPay.Application.Common;

/// <summary>OCR 识别的银行/账号/持有人/公司/地址 与客户主数据期望字段比对。</summary>
public static class CheckMasterDataComparer
{
    public static bool HasMismatch(
        Customer customer,
        string? ocrBankName,
        string? ocrAccountHolderName,
        string? ocrCompanyName = null,
        string? ocrAccountNumber = null,
        string? ocrAccountAddress = null)
    {
        static string Norm(string? s) =>
            string.IsNullOrWhiteSpace(s)
                ? ""
                : string.Concat(s.Where(ch => !char.IsWhiteSpace(ch))).ToLowerInvariant();

        static string NormDigits(string? s) =>
            string.IsNullOrWhiteSpace(s)
                ? ""
                : string.Concat(s.Where(char.IsDigit));

        var b = Norm(ocrBankName);
        var h = Norm(ocrAccountHolderName);
        var co = Norm(ocrCompanyName);
        var acct = NormDigits(ocrAccountNumber);
        var addr = Norm(ocrAccountAddress);

        if (!string.IsNullOrEmpty(customer.ExpectedBankName) && !string.IsNullOrEmpty(b))
        {
            var eb = Norm(customer.ExpectedBankName);
            if (eb != b && !eb.Contains(b) && !b.Contains(eb))
                return true;
        }

        if (!string.IsNullOrEmpty(customer.CustomerCode) && !string.IsNullOrEmpty(acct))
        {
            var codeDigits = NormDigits(customer.CustomerCode);
            if (codeDigits != acct && !codeDigits.Contains(acct) && !acct.Contains(codeDigits))
                return true;
        }

        if (!string.IsNullOrEmpty(customer.ExpectedAccountHolderName) && !string.IsNullOrEmpty(h))
        {
            var eh = Norm(customer.ExpectedAccountHolderName);
            if (eh != h && !eh.Contains(h) && !h.Contains(eh))
                return true;
        }

        if (!string.IsNullOrEmpty(customer.ExpectedCompanyName) && !string.IsNullOrEmpty(co))
        {
            var ec = Norm(customer.ExpectedCompanyName);
            if (ec != co && !ec.Contains(co) && !co.Contains(ec))
                return true;
        }

        if (!string.IsNullOrEmpty(customer.ExpectedAccountAddress) && !string.IsNullOrEmpty(addr))
        {
            var ead = Norm(customer.ExpectedAccountAddress);
            if (ead != addr && !ead.Contains(addr) && !addr.Contains(ead))
                return true;
        }

        return false;
    }
}
