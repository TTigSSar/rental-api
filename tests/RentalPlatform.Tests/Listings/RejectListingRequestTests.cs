using System.ComponentModel.DataAnnotations;
using RentalPlatform.Application.Common;
using RentalPlatform.Application.DTOs;
using Xunit;

namespace RentalPlatform.Tests.Listings;

// The admin reject payload accepts a structured { reasonCode, note } and the catalog composes
// the human-readable reason persisted on the listing.
public sealed class RejectListingRequestTests
{
    private static IReadOnlyList<ValidationResult> Validate(RejectListingRequest request)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(request, new ValidationContext(request), results, validateAllProperties: true);
        return results;
    }

    [Fact]
    public void Composes_Reason_From_Code_And_Note()
    {
        var request = new RejectListingRequest { ReasonCode = "missingInfo", Note = "  Test Reason  " };

        Assert.Empty(Validate(request));
        Assert.Equal(
            "Missing or incomplete information: Test Reason",
            RejectionReasonCatalog.Compose(request.ReasonCode, request.Note));
    }

    [Fact]
    public void Composes_Reason_From_Code_When_Note_Absent()
    {
        var request = new RejectListingRequest { ReasonCode = "unsafeItem", Note = null };

        Assert.Empty(Validate(request));
        Assert.Equal("Unsafe item", RejectionReasonCatalog.Compose(request.ReasonCode, request.Note));
    }

    [Fact]
    public void Rejects_Unknown_Reason_Code()
    {
        var request = new RejectListingRequest { ReasonCode = "bogus" };

        var results = Validate(request);

        Assert.Contains(results, r => r.MemberNames.Contains(nameof(RejectListingRequest.ReasonCode)));
    }

    [Fact]
    public void Rejects_Missing_Reason_Code()
    {
        var request = new RejectListingRequest { ReasonCode = "" };

        Assert.NotEmpty(Validate(request));
    }
}
