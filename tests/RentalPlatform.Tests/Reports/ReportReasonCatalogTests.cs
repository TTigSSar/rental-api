using RentalPlatform.Application.Common;
using RentalPlatform.Domain.Enums;
using Xunit;

namespace RentalPlatform.Tests.Reports;

// Admin console Phase 4: the reason -> severity table is derived server-side, never
// client-supplied (see ReportsService.CreateAsync) — these tests pin down every mapping.
public sealed class ReportReasonCatalogTests
{
    [Theory]
    [InlineData("noShow", ReportSeverity.High)]
    [InlineData("unsafeItem", ReportSeverity.High)]
    [InlineData("misleadingPhotos", ReportSeverity.Medium)]
    [InlineData("offPlatformPayment", ReportSeverity.Medium)]
    [InlineData("rudeMessages", ReportSeverity.Low)]
    [InlineData("spam", ReportSeverity.Medium)]
    [InlineData("other", ReportSeverity.Low)]
    public void SeverityFor_Maps_Every_Known_Reason_Code(string code, ReportSeverity expected)
    {
        Assert.Equal(expected, ReportReasonCatalog.SeverityFor(code));
    }

    [Theory]
    [InlineData("noShow")]
    [InlineData("unsafeItem")]
    [InlineData("misleadingPhotos")]
    [InlineData("offPlatformPayment")]
    [InlineData("rudeMessages")]
    [InlineData("spam")]
    [InlineData("other")]
    public void IsKnownCode_True_For_Every_Catalog_Entry(string code)
    {
        Assert.True(ReportReasonCatalog.IsKnownCode(code));
    }

    [Theory]
    [InlineData("noShow", "No-show at pickup")]
    [InlineData("unsafeItem", "Unsafe or broken item")]
    [InlineData("misleadingPhotos", "Misleading photos")]
    [InlineData("offPlatformPayment", "Off-platform payment attempt")]
    [InlineData("rudeMessages", "Rude or abusive messages")]
    [InlineData("spam", "Spam or scam")]
    [InlineData("other", "Other")]
    public void LabelFor_Matches_The_Documented_Vocabulary(string code, string expectedLabel)
    {
        Assert.Equal(expectedLabel, ReportReasonCatalog.LabelFor(code));
    }

    [Fact]
    public void IsKnownCode_False_For_Unknown_Code()
    {
        Assert.False(ReportReasonCatalog.IsKnownCode("bogus"));
    }

    [Fact]
    public void IsKnownCode_False_For_Null_Or_Whitespace()
    {
        Assert.False(ReportReasonCatalog.IsKnownCode(null));
        Assert.False(ReportReasonCatalog.IsKnownCode("   "));
    }

    [Fact]
    public void IsKnownCode_Is_Case_Insensitive()
    {
        Assert.True(ReportReasonCatalog.IsKnownCode("NOSHOW"));
        Assert.True(ReportReasonCatalog.IsKnownCode("UnsafeItem"));
    }

    [Fact]
    public void SeverityFor_Unknown_Code_Falls_Back_To_Low()
    {
        Assert.Equal(ReportSeverity.Low, ReportReasonCatalog.SeverityFor("bogus"));
    }

    [Fact]
    public void CodesMatchingLabel_Matches_Case_Insensitively_Against_The_Label_Text()
    {
        var codes = ReportReasonCatalog.CodesMatchingLabel("pickup");

        Assert.Contains("noShow", codes); // "No-show at pickup"
        Assert.DoesNotContain("unsafeItem", codes);
    }

    [Fact]
    public void CodesMatchingLabel_Returns_Empty_When_Nothing_Matches()
    {
        Assert.Empty(ReportReasonCatalog.CodesMatchingLabel("zzz-no-such-term"));
    }
}
