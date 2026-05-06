using System.Text.Json;
using CheckPay.Application.Common;
using CheckPay.Application.Common.Interfaces;
using CheckPay.Domain.Enums;
using Xunit;

namespace CheckPay.Tests.Application;

public class OcrRawResultAmountValidationDiagnosticsTests
{
    [Fact]
    public void MergeIntoRawJson_PreservesExistingDiagnosticsAndWritesHandwrittenFields()
    {
        var raw = JsonDocument.Parse("""{"ExtractedText":"x","Diagnostics":{"foo":"bar"}}""");
        var validation = new AmountValidationResult(
            10148m,
            10148m,
            "Ten thousand one hundred",
            true,
            0.8844,
            "completed",
            null);
        var avJson = JsonDocument.Parse(JsonSerializer.Serialize(validation));

        var merged = OcrRawResultAmountValidationDiagnostics.MergeIntoRawJson(
            raw,
            avJson,
            AmountValidationStatus.Completed,
            null);

        using (merged)
        {
            var diag = merged.RootElement.GetProperty("Diagnostics");
            Assert.Equal("bar", diag.GetProperty("foo").GetString());
            Assert.Equal("Completed", diag.GetProperty("di_amount_validation_status").GetString());
            Assert.Equal("completed", diag.GetProperty("di_handwritten_di_service_status").GetString());
            Assert.Equal("Ten thousand one hundred", diag.GetProperty("di_handwritten_legal_amount_raw").GetString());
            Assert.Equal("10148.00", diag.GetProperty("di_handwritten_legal_amount_parsed").GetString());
            Assert.Equal("0.8844", diag.GetProperty("di_handwritten_validation_confidence").GetString());
            Assert.Equal("True", diag.GetProperty("di_handwritten_is_consistent").GetString());
        }
    }
}
