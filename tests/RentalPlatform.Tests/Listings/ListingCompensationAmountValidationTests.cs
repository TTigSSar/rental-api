using System.ComponentModel.DataAnnotations;
using RentalPlatform.Application.DTOs;
using Xunit;

namespace RentalPlatform.Tests.Listings;

// CompensationAmount ("Loss & damage compensation") is what the renter owes the owner if the toy
// is lost, seriously damaged, or not returned — nothing is ever paid upfront or held by DoRent
// (ADR-014). On create it is required; on update it is optional (partial update) but must fall in
// the same [1000, 10000000] AMD range when supplied. No whole-number constraint here — the UI
// enforces integers.
public sealed class ListingCompensationAmountValidationTests
{
    private static IReadOnlyList<ValidationResult> Validate(object request)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(request, new ValidationContext(request), results, validateAllProperties: true);
        return results;
    }

    private static CreateListingRequest ValidCreate(decimal? compensationAmount) => new()
    {
        CategoryId = Guid.NewGuid(),
        Title = "Wooden Train Set",
        Description = "A long enough description to satisfy validation rules.",
        PricePerDay = 12m,
        Country = "Armenia",
        City = "Yerevan",
        CompensationAmount = compensationAmount
    };

    [Fact]
    public void Create_Rejects_Omitted_CompensationAmount()
    {
        var results = Validate(ValidCreate(null));

        Assert.Contains(results, r => r.MemberNames.Contains(nameof(CreateListingRequest.CompensationAmount)));
    }

    [Fact]
    public void Create_Rejects_Below_Minimum()
    {
        var results = Validate(ValidCreate(999m));

        Assert.Contains(results, r => r.MemberNames.Contains(nameof(CreateListingRequest.CompensationAmount)));
    }

    [Fact]
    public void Create_Rejects_Above_Maximum()
    {
        var results = Validate(ValidCreate(10_000_001m));

        Assert.Contains(results, r => r.MemberNames.Contains(nameof(CreateListingRequest.CompensationAmount)));
    }

    [Fact]
    public void Create_Accepts_Lower_Boundary()
    {
        Assert.Empty(Validate(ValidCreate(1000m)));
    }

    [Fact]
    public void Create_Accepts_Upper_Boundary()
    {
        Assert.Empty(Validate(ValidCreate(10_000_000m)));
    }

    [Fact]
    public void Update_Accepts_Omitted_CompensationAmount()
    {
        var request = new UpdateListingRequest { CompensationAmount = null };

        Assert.Empty(Validate(request));
    }

    [Fact]
    public void Update_Rejects_Below_Minimum()
    {
        var request = new UpdateListingRequest { CompensationAmount = 999m };

        var results = Validate(request);

        Assert.Contains(results, r => r.MemberNames.Contains(nameof(UpdateListingRequest.CompensationAmount)));
    }

    [Fact]
    public void Update_Rejects_Above_Maximum()
    {
        var request = new UpdateListingRequest { CompensationAmount = 10_000_001m };

        var results = Validate(request);

        Assert.Contains(results, r => r.MemberNames.Contains(nameof(UpdateListingRequest.CompensationAmount)));
    }

    [Fact]
    public void Update_Accepts_Boundaries()
    {
        Assert.Empty(Validate(new UpdateListingRequest { CompensationAmount = 1000m }));
        Assert.Empty(Validate(new UpdateListingRequest { CompensationAmount = 10_000_000m }));
    }
}
