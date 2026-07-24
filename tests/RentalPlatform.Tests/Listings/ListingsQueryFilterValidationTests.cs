using System.ComponentModel.DataAnnotations;
using RentalPlatform.Application.DTOs;
using Xunit;

namespace RentalPlatform.Tests.Listings;

// Maps P2-1: the viewport bounding box must be rejected, not silently emptied, when it crosses
// the antimeridian (MinLng > MaxLng). Yerevan is nowhere near it, so a caller sending this is
// either a client bug or a malformed hand-built request — either way it should surface as a
// validation error, mirroring the RejectListingRequest IValidatableObject pattern.
public sealed class ListingsQueryFilterValidationTests
{
    private static IReadOnlyList<ValidationResult> Validate(ListingsQueryFilter filter)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(filter, new ValidationContext(filter), results, validateAllProperties: true);
        return results;
    }

    [Fact]
    public void Rejects_MinLng_Greater_Than_MaxLng()
    {
        var filter = new ListingsQueryFilter { MinLng = 44.60m, MaxLng = 44.45m };

        var results = Validate(filter);

        Assert.Contains(results, r => r.MemberNames.Contains(nameof(ListingsQueryFilter.MinLng)));
    }

    [Fact]
    public void Accepts_MinLng_Less_Than_Or_Equal_To_MaxLng()
    {
        var filter = new ListingsQueryFilter { MinLng = 44.45m, MaxLng = 44.60m };

        Assert.Empty(Validate(filter));
    }

    [Fact]
    public void Accepts_MinLng_Equal_To_MaxLng()
    {
        var filter = new ListingsQueryFilter { MinLng = 44.50m, MaxLng = 44.50m };

        Assert.Empty(Validate(filter));
    }

    [Fact]
    public void Ignores_Longitude_Order_When_Only_One_Bound_Supplied()
    {
        var filter = new ListingsQueryFilter { MinLng = 44.60m };

        Assert.Empty(Validate(filter));
    }
}
