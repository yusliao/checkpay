using CheckPay.Application.Common;

namespace CheckPay.Tests.Application;

public class CustomerMasterSyncTests
{
    [Fact]
    public void ApplyToCheckFormFields_RowMissing_CompositeUnchanged_DoesNotClearPhone()
    {
        string? lastCode = null;
        string? lastRk = null;
        var phone = "555";
        var bank = "";
        var company = "";
        var holder = "";
        var addr = "";
        var acctType = "";
        var payTo = "";

        CustomerMasterSync.ApplyToCheckFormFields(null, "acct", "021000021", ref lastCode, ref lastRk,
            ref phone, ref bank, ref company, ref holder, ref addr, ref acctType, ref payTo);

        Assert.Equal("555", phone);
        Assert.Null(lastCode);
    }

    [Fact]
    public void ApplyToCheckFormFields_RowMissing_CompositeChanged_ClearsPhoneAndResetsLast()
    {
        string? lastCode = "old";
        string? lastRk = "021000021";
        var phone = "555";
        var bank = "B";
        var company = "";
        var holder = "";
        var addr = "";
        var acctType = "";
        var payTo = "";

        CustomerMasterSync.ApplyToCheckFormFields(null, "newAcct", "021000021", ref lastCode, ref lastRk,
            ref phone, ref bank, ref company, ref holder, ref addr, ref acctType, ref payTo);

        Assert.Equal("", phone);
        Assert.Null(lastCode);
        Assert.Null(lastRk);
        Assert.Equal("B", bank);
    }

    [Fact]
    public void ApplyToCheckFormFields_RowFound_FillsEmptyBankAndCompany_FromExpectedFields()
    {
        string? lastCode = null;
        string? lastRk = null;
        var phone = "";
        var bank = "";
        var company = "";
        var holder = "";
        var addr = "123 Main";
        var acctType = "";
        var payTo = "";

        var row = new CustomerMasterSync.MasterRow(
            MobilePhone: "111",
            ExpectedBankName: " Truist ",
            ExpectedCompanyName: "Acme LLC",
            ExpectedAccountHolderName: null,
            ExpectedAccountAddress: null,
            ExpectedAccountType: null,
            ExpectedPayToOrderOf: null);

        CustomerMasterSync.ApplyToCheckFormFields(row, "acct", "021000021", ref lastCode, ref lastRk,
            ref phone, ref bank, ref company, ref holder, ref addr, ref acctType, ref payTo);

        Assert.Equal("111", phone);
        Assert.Equal("Truist", bank);
        Assert.Equal("Acme LLC", company);
        Assert.Equal("Acme LLC", holder);
        Assert.Equal("123 Main", addr);
        Assert.Equal("acct", lastCode);
        Assert.Equal("021000021", lastRk);
    }

    [Fact]
    public void ApplyToCheckFormFields_SecondCallSameComposite_DoesNotOverwriteUserBank()
    {
        string? lastCode = "acct";
        string? lastRk = "021000021";
        var phone = "111";
        var bank = "User Bank";
        var company = "";
        var holder = "";
        var addr = "";
        var acctType = "";
        var payTo = "";

        var row = new CustomerMasterSync.MasterRow("111", " Truist ", "C", null, null, null, null);

        CustomerMasterSync.ApplyToCheckFormFields(row, "acct", "021000021", ref lastCode, ref lastRk,
            ref phone, ref bank, ref company, ref holder, ref addr, ref acctType, ref payTo);

        Assert.Equal("User Bank", bank);
        Assert.Equal("acct", lastCode);
    }

    [Fact]
    public void ApplyToCheckFormFields_CompositeChanged_ReappliesMasterBank()
    {
        string? lastCode = "acct";
        string? lastRk = "021000021";
        var phone = "111";
        var bank = "Old";
        var company = "";
        var holder = "";
        var addr = "";
        var acctType = "";
        var payTo = "";

        var row = new CustomerMasterSync.MasterRow("111", "Chase", null, null, null, null, null);

        CustomerMasterSync.ApplyToCheckFormFields(row, "acct", "011000015", ref lastCode, ref lastRk,
            ref phone, ref bank, ref company, ref holder, ref addr, ref acctType, ref payTo);

        Assert.Equal("Chase", bank);
        Assert.Equal("acct", lastCode);
        Assert.Equal("011000015", lastRk);
    }

    [Fact]
    public void ApplyToCheckFormFields_MasterPhoneEmpty_CompositeChanged_ClearsFormPhone()
    {
        string? lastCode = "a";
        string? lastRk = "021000021";
        var phone = "999";
        var bank = "";
        var company = "";
        var holder = "";
        var addr = "";
        var acctType = "";
        var payTo = "";

        var row = new CustomerMasterSync.MasterRow("", null, null, null, "Somewhere", null, null);

        CustomerMasterSync.ApplyToCheckFormFields(row, "b", "021000021", ref lastCode, ref lastRk,
            ref phone, ref bank, ref company, ref holder, ref addr, ref acctType, ref payTo);

        Assert.Equal("", phone);
        Assert.Equal("Somewhere", addr);
    }

    [Fact]
    public void ApplyToCheckFormFields_FillsAccountTypeAndPayTo_WhenEmpty()
    {
        string? lastCode = null;
        string? lastRk = null;
        var phone = "1";
        var bank = "";
        var company = "";
        var holder = "";
        var addr = "";
        var acctType = "";
        var payTo = "";

        var row = new CustomerMasterSync.MasterRow(
            "1", null, null, null, null,
            "business checking",
            PayToOrderOfCatalog.MaxwellExcelFood);

        CustomerMasterSync.ApplyToCheckFormFields(row, "a", "021000021", ref lastCode, ref lastRk,
            ref phone, ref bank, ref company, ref holder, ref addr, ref acctType, ref payTo);

        Assert.Equal(CheckAccountTypeCatalog.BusinessChecking, acctType);
        Assert.Equal(PayToOrderOfCatalog.MaxwellExcelFood, payTo);
    }

    [Fact]
    public void CanonicalAccountTypeForCustomerMaster_MapsCheckingSynonym()
    {
        Assert.Equal(CheckAccountTypeCatalog.Savings, CustomerMasterSync.CanonicalAccountTypeForCustomerMaster( "savings account"));
        Assert.Null(CustomerMasterSync.CanonicalAccountTypeForCustomerMaster("weird"));
    }

    [Fact]
    public void CanonicalPayToForCustomerMaster_AcceptsFullName()
    {
        Assert.Equal(
            PayToOrderOfCatalog.CheungKongAllianceFood,
            CustomerMasterSync.CanonicalPayToForCustomerMaster(PayToOrderOfCatalog.CheungKongAllianceFood));
        Assert.Null(CustomerMasterSync.CanonicalPayToForCustomerMaster("unknown payee"));
    }
}
