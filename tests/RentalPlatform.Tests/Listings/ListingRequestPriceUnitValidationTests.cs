using System.ComponentModel.DataAnnotations;
using RentalPlatform.Application.DTOs;
using RentalPlatform.Domain.Enums;
using Xunit;

namespace RentalPlatform.Tests.Listings;

// priceUnit is optional on the create/update payloads but, when supplied, must be a defined
// PriceUnit value. Omitting it is valid (the service then defaults to Daily).
public sealed class ListingRequestPriceUnitValidationTests
{
    private static IReadOnlyList<ValidationResult> Validate(object request)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(request, new ValidationContext(request), results, validateAllProperties: true);
        return results;
    }

    private static CreateListingRequest ValidCreate(PriceUnit? priceUnit) => new()
    {
        CategoryId = Guid.NewGuid(),
        Title = "Wooden Train Set",
        Description = "A long enough description to satisfy validation rules.",
        PricePerDay = 12m,
        PriceUnit = priceUnit,
        Country = "Armenia",
        City = "Yerevan"
    };

    [Fact]
    public void Create_Accepts_Defined_PriceUnit()
    {
        Assert.Empty(Validate(ValidCreate(PriceUnit.Weekly)));
    }

    [Fact]
    public void Create_Accepts_Omitted_PriceUnit()
    {
        Assert.Empty(Validate(ValidCreate(null)));
    }

    [Fact]
    public void Create_Rejects_Undefined_PriceUnit()
    {
        var request = ValidCreate((PriceUnit)99);

        var results = Validate(request);

        Assert.Contains(results, r => r.MemberNames.Contains(nameof(CreateListingRequest.PriceUnit)));
    }

    [Fact]
    public void Update_Rejects_Undefined_PriceUnit()
    {
        var request = new UpdateListingRequest { PriceUnit = (PriceUnit)99 };

        var results = Validate(request);

        Assert.Contains(results, r => r.MemberNames.Contains(nameof(UpdateListingRequest.PriceUnit)));
    }
}
