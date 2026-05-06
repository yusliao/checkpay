using CheckPay.Application.Common;
using CheckPay.Domain.Entities;

namespace CheckPay.Tests.Business;

public class CustomerCsvImportExportTests
{
    [Fact]
    public void Parse_accepts_legacy_expected_address_column_header()
    {
        var legacyHeader =
            "客户账号,客户名称,手机号,关联银行,票面公司名称,期望地址,关联公司,活跃,已授权";
        var csv = legacyHeader + Environment.NewLine
                  + "acc1,名称1,13800000001,银行,公司,地址,司A|司B,1,0";
        var (rows, errors) = CustomerCsvImportExport.Parse(csv);
        Assert.Empty(errors);
        Assert.Single(rows);
        Assert.Equal("地址", rows[0].ExpectedAccountAddress);
        Assert.Equal("", rows[0].ExpectedRoutingNumber);
    }

    [Fact]
    public void Parse_accepts_legacy_restaurant_id_column_header_alias()
    {
        var legacyHeader =
            "客户账号,客户名称,餐馆编号,关联银行,票面公司名称,期望地址,关联公司,活跃,已授权";
        var csv = legacyHeader + Environment.NewLine
                  + "acc1,名称1,13800000001,银行,公司,地址,司A|司B,1,0";
        var (rows, errors) = CustomerCsvImportExport.Parse(csv);
        Assert.Empty(errors);
        Assert.Single(rows);
        Assert.Equal("13800000001", rows[0].MobilePhone);
    }

    [Fact]
    public void Parse_accepts_chinese_header_and_one_row()
    {
        var csv = CustomerCsvImportExport.Header + Environment.NewLine
                  + "acc1,,名称1,13800000001,银行,公司,地址,司A|司B,1,0";
        var (rows, errors) = CustomerCsvImportExport.Parse(csv);
        Assert.Empty(errors);
        Assert.Single(rows);
        Assert.Equal("acc1", rows[0].CustomerCode);
        Assert.Equal("", rows[0].ExpectedRoutingNumber);
        Assert.Equal("名称1", rows[0].CustomerName);
        Assert.Equal("13800000001", rows[0].MobilePhone);
        Assert.Equal("银行", rows[0].ExpectedBankName);
        Assert.Equal(2, CustomerCsvImportExport.ParseCompanyNamesCell(rows[0].CompanyNamesRaw).Count);
    }

    [Fact]
    public void Parse_rejects_duplicate_code_and_routing_combo_in_file()
    {
        var csv = CustomerCsvImportExport.Header + Environment.NewLine
                  + "acc1,021000021,名称1,13800000001,银行,公司,地址,,1,0" + Environment.NewLine
                  + "acc1,021000021,名称2,13800000002,银行,公司,地址,,1,0";
        var (_, errors) = CustomerCsvImportExport.Parse(csv);
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void Export_roundtrip_fields()
    {
        var c = new Customer
        {
            CustomerCode = "C1",
            ExpectedRoutingNumber = "",
            CustomerName = "N1",
            MobilePhone = "13900000000",
            ExpectedBankName = "B1",
            ExpectedCompanyName = "O1",
            ExpectedAccountHolderName = "O1",
            ExpectedAccountAddress = "A1",
            ExpectedAccountType = CheckAccountTypeCatalog.Savings,
            ExpectedPayToOrderOf = PayToOrderOfCatalog.CheungKongAllianceFood,
            IsActive = true,
            IsAuthorized = false,
            CompanyNames = new List<CustomerCompanyName>
            {
                new() { CompanyName = "X1" },
                new() { CompanyName = "X2" }
            }
        };
        var bytes = CustomerCsvImportExport.ExportToUtf8BomBytes(new[] { c });
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        var (rows, errors) = CustomerCsvImportExport.Parse(text);
        Assert.Empty(errors);
        Assert.Single(rows);
        Assert.Equal("C1", rows[0].CustomerCode);
        Assert.Equal("", rows[0].ExpectedRoutingNumber);
        Assert.Equal("13900000000", rows[0].MobilePhone);
        Assert.False(rows[0].IsAuthorized);
        Assert.True(rows[0].IsActive);
        Assert.Equal(CheckAccountTypeCatalog.Savings, rows[0].ExpectedAccountType);
        Assert.Equal(PayToOrderOfCatalog.CheungKongAllianceFood, rows[0].ExpectedPayToOrderOf);
    }
}
