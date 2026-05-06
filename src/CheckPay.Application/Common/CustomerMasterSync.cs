namespace CheckPay.Application.Common;

/// <summary>支票上传/复核表单：按「账号 + ABA」命中客户主数据时，将手机号与期望票面字段（含账户类型、Pay to）写入表单（不随意覆盖用户在同组合下的手工修改）。</summary>
public static class CustomerMasterSync
{
    public readonly record struct MasterRow(
        string? MobilePhone,
        string? ExpectedBankName,
        string? ExpectedCompanyName,
        string? ExpectedAccountHolderName,
        string? ExpectedAccountAddress,
        string? ExpectedAccountType,
        string? ExpectedPayToOrderOf);

    /// <summary>
    /// <paramref name="row"/> 为 null 表示未命中主数据；行为与原先仅同步手机号时一致：仅在「上次曾成功同步」且当前账号+ABA 相对上次变化时清空手机号。
    /// 命中主数据时：各字段在表单为空或账号+ABA 相对上次成功同步已变化时，用主数据非空值填充。
    /// </summary>
    public static void ApplyToCheckFormFields(
        MasterRow? row,
        string customerCodeTrimmed,
        string routingKeyNormalized,
        ref string? lastSyncedCode,
        ref string? lastSyncedRoutingKey,
        ref string customerPhone,
        ref string bankName,
        ref string companyName,
        ref string accountHolderName,
        ref string accountAddress,
        ref string accountType,
        ref string payToOrderOf)
    {
        var code = customerCodeTrimmed;
        var rk = routingKeyNormalized;

        if (row == null)
        {
            if (lastSyncedCode != null
                && (!string.Equals(lastSyncedCode, code, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(lastSyncedRoutingKey ?? "", rk, StringComparison.Ordinal)))
                customerPhone = string.Empty;

            lastSyncedCode = null;
            lastSyncedRoutingKey = null;
            return;
        }

        var rowVal = row.Value;
        var compositeChanged = lastSyncedCode != null
                               && (!string.Equals(lastSyncedCode, code, StringComparison.OrdinalIgnoreCase)
                                   || !string.Equals(lastSyncedRoutingKey ?? "", rk, StringComparison.Ordinal));

        var applied = false;

        if (!string.IsNullOrWhiteSpace(rowVal.MobilePhone))
        {
            if (string.IsNullOrWhiteSpace(customerPhone) || compositeChanged)
            {
                customerPhone = rowVal.MobilePhone.Trim();
                applied = true;
            }
        }
        else if (compositeChanged)
        {
            customerPhone = string.Empty;
            applied = true;
        }

        void ApplyOptional(ref string target, string? master)
        {
            if (string.IsNullOrWhiteSpace(master)) return;
            if (string.IsNullOrWhiteSpace(target) || compositeChanged)
            {
                target = master.Trim();
                applied = true;
            }
        }

        ApplyOptional(ref bankName, rowVal.ExpectedBankName);
        ApplyOptional(ref accountAddress, rowVal.ExpectedAccountAddress);

        if (!string.IsNullOrWhiteSpace(rowVal.ExpectedAccountType))
        {
            var mappedAcct = CheckAccountTypeCatalog.MatchSalesSelectable(rowVal.ExpectedAccountType);
            if (mappedAcct != null
                && (string.IsNullOrWhiteSpace(accountType) || compositeChanged))
            {
                accountType = mappedAcct;
                applied = true;
            }
        }

        if (!string.IsNullOrWhiteSpace(rowVal.ExpectedPayToOrderOf))
        {
            var canonicalPayTo = PayToOrderOfCatalog.MatchCanonical(rowVal.ExpectedPayToOrderOf)
                                 ?? PayToOrderOfCatalog.All.FirstOrDefault(a =>
                                     a.Equals(rowVal.ExpectedPayToOrderOf.Trim(), StringComparison.OrdinalIgnoreCase));
            if (canonicalPayTo != null
                && (string.IsNullOrWhiteSpace(payToOrderOf) || compositeChanged))
            {
                payToOrderOf = canonicalPayTo;
                applied = true;
            }
        }

        var mergedHolderCompany = OcrCheckCustomerFields.MergeHolderCompanyDisplayName(
            rowVal.ExpectedCompanyName,
            rowVal.ExpectedAccountHolderName,
            null);
        if (!string.IsNullOrEmpty(mergedHolderCompany))
        {
            if (string.IsNullOrWhiteSpace(companyName) || compositeChanged)
            {
                companyName = mergedHolderCompany;
                applied = true;
            }

            if (string.IsNullOrWhiteSpace(accountHolderName) || compositeChanged)
            {
                accountHolderName = mergedHolderCompany;
                applied = true;
            }
        }

        if (applied)
        {
            lastSyncedCode = code;
            lastSyncedRoutingKey = rk;
        }
    }

    /// <summary>写入客户主数据前将表单/CSV 账户类型归一为销售可选值；无法映射则返回 null。</summary>
    public static string? CanonicalAccountTypeForCustomerMaster(string? raw)
        => string.IsNullOrWhiteSpace(raw) ? null : CheckAccountTypeCatalog.MatchSalesSelectable(raw);

    /// <summary>写入客户主数据前将 Pay to 归一为目录规范全称；无法映射则返回 null。</summary>
    public static string? CanonicalPayToForCustomerMaster(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var t = raw.Trim();
        return PayToOrderOfCatalog.MatchCanonical(t)
               ?? PayToOrderOfCatalog.All.FirstOrDefault(a => a.Equals(t, StringComparison.OrdinalIgnoreCase));
    }
}
