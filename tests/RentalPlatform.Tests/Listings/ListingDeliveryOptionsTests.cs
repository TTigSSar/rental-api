using System.ComponentModel.DataAnnotations;
using RentalPlatform.Application.Common;
using RentalPlatform.Application.DTOs;
using RentalPlatform.Domain.Enums;
using Xunit;

namespace RentalPlatform.Tests.Listings;

// DeliveryTypes is the additive multi-select successor to the legacy scalar DeliveryType (see
// DeliveryOptionsMapper). These tests cover the DTO-level validation (IValidatableObject on
// Create/UpdateListingRequest) and the shared flags<->list mapping helper in isolation from the
// service/store plumbing exercised by ListingsOwnerServiceTests.
public sealed class ListingDeliveryOptionsTests
{
    private static IReadOnlyList<ValidationResult> Validate(object request)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(request, new ValidationContext(request), results, validateAllProperties: true);
        return results;
    }

    private static CreateListingRequest ValidCreate(
        IReadOnlyList<DeliveryType>? deliveryTypes = null,
        int? minRentalDays = null) => new()
    {
        CategoryId = Guid.NewGuid(),
        Title = "Wooden Train Set",
        Description = "A long enough description to satisfy validation rules.",
        PricePerDay = 12m,
        Country = "Armenia",
        City = "Yerevan",
        CompensationAmount = 5000m,
        DeliveryTypes = deliveryTypes,
        MinRentalDays = minRentalDays
    };

    [Fact]
    public void Create_Accepts_Omitted_DeliveryTypes()
    {
        Assert.Empty(Validate(ValidCreate()));
    }

    [Fact]
    public void Create_Accepts_NonEmpty_DeliveryTypes()
    {
        Assert.Empty(Validate(ValidCreate(new[] { DeliveryType.Pickup, DeliveryType.Courier })));
    }

    [Fact]
    public void Create_Rejects_Empty_DeliveryTypes_List()
    {
        var results = Validate(ValidCreate(Array.Empty<DeliveryType>()));

        Assert.Contains(results, r => r.MemberNames.Contains(nameof(CreateListingRequest.DeliveryTypes)));
    }

    [Fact]
    public void Create_Rejects_Undefined_Value_In_DeliveryTypes()
    {
        var results = Validate(ValidCreate(new[] { (DeliveryType)99 }));

        Assert.Contains(results, r => r.MemberNames.Contains(nameof(CreateListingRequest.DeliveryTypes)));
    }

    [Fact]
    public void Update_Accepts_Omitted_DeliveryTypes()
    {
        Assert.Empty(Validate(new UpdateListingRequest { DeliveryTypes = null }));
    }

    [Fact]
    public void Update_Rejects_Empty_DeliveryTypes_List()
    {
        var request = new UpdateListingRequest { DeliveryTypes = Array.Empty<DeliveryType>() };

        var results = Validate(request);

        Assert.Contains(results, r => r.MemberNames.Contains(nameof(UpdateListingRequest.DeliveryTypes)));
    }

    [Fact]
    public void Update_Rejects_Undefined_Value_In_DeliveryTypes()
    {
        var request = new UpdateListingRequest { DeliveryTypes = new[] { (DeliveryType)99 } };

        var results = Validate(request);

        Assert.Contains(results, r => r.MemberNames.Contains(nameof(UpdateListingRequest.DeliveryTypes)));
    }

    // [Range(1, 365)] on MinRentalDays already permits 365 (the max explicitly offered by the
    // create-listing wizard's duration presets); this pins that behaviour so a future narrowing of
    // the range attribute gets caught.
    [Fact]
    public void Create_Accepts_MinRentalDays_365()
    {
        Assert.Empty(Validate(ValidCreate(minRentalDays: 365)));
    }

    [Fact]
    public void Update_Accepts_MinRentalDays_365()
    {
        Assert.Empty(Validate(new UpdateListingRequest { MinRentalDays = 365 }));
    }

    [Fact]
    public void Combine_Prefers_DeliveryTypes_Over_Legacy_When_Both_Supplied()
    {
        var flags = DeliveryOptionsMapper.Combine(new[] { DeliveryType.Courier }, DeliveryType.Pickup);

        Assert.Equal(DeliveryOptions.Courier, flags);
    }

    [Fact]
    public void Combine_Ors_Multiple_DeliveryTypes()
    {
        var flags = DeliveryOptionsMapper.Combine(new[] { DeliveryType.Pickup, DeliveryType.Courier }, null);

        Assert.Equal(DeliveryOptions.Pickup | DeliveryOptions.Courier, flags);
    }

    [Fact]
    public void Combine_Falls_Back_To_Legacy_When_DeliveryTypes_Omitted()
    {
        var flags = DeliveryOptionsMapper.Combine(null, DeliveryType.Courier);

        Assert.Equal(DeliveryOptions.Courier, flags);
    }

    [Fact]
    public void Combine_Returns_Null_When_Neither_Supplied()
    {
        Assert.Null(DeliveryOptionsMapper.Combine(null, null));
    }

    [Fact]
    public void ToLegacy_Prefers_Pickup_When_Both_Flags_Set()
    {
        Assert.Equal(DeliveryType.Pickup, DeliveryOptionsMapper.ToLegacy(DeliveryOptions.Pickup | DeliveryOptions.Courier));
    }

    [Fact]
    public void ToLegacy_Returns_Courier_When_Only_Courier_Flag_Set()
    {
        Assert.Equal(DeliveryType.Courier, DeliveryOptionsMapper.ToLegacy(DeliveryOptions.Courier));
    }

    [Fact]
    public void ToLegacy_Returns_Null_When_Flags_Null_Or_None()
    {
        Assert.Null(DeliveryOptionsMapper.ToLegacy(null));
        Assert.Null(DeliveryOptionsMapper.ToLegacy(DeliveryOptions.None));
    }

    [Fact]
    public void Expand_Returns_Ordered_Pickup_Then_Courier()
    {
        var expanded = DeliveryOptionsMapper.Expand(DeliveryOptions.Courier | DeliveryOptions.Pickup, null);

        Assert.Equal(new[] { DeliveryType.Pickup, DeliveryType.Courier }, expanded);
    }

    [Fact]
    public void Expand_Falls_Back_To_Single_Element_List_From_Legacy_When_Flags_Absent()
    {
        var expanded = DeliveryOptionsMapper.Expand(null, DeliveryType.Courier);

        Assert.Equal(new[] { DeliveryType.Courier }, expanded);
    }

    [Fact]
    public void Expand_Returns_Null_When_Both_Absent()
    {
        Assert.Null(DeliveryOptionsMapper.Expand(null, null));
    }

    // ShouldApplyLegacyOnlyUpdate backs the update-path fix for the stale-client bug: a legacy-only
    // DeliveryType submission must be treated as a no-op when it's already reflected in the current
    // flags, and applied (collapsing to the single flag) only when it's a real change.
    [Fact]
    public void ShouldApplyLegacyOnlyUpdate_False_When_Current_Flags_Already_Include_Legacy_Value()
    {
        Assert.False(DeliveryOptionsMapper.ShouldApplyLegacyOnlyUpdate(
            DeliveryOptions.Pickup | DeliveryOptions.Courier, DeliveryType.Pickup));
        Assert.False(DeliveryOptionsMapper.ShouldApplyLegacyOnlyUpdate(
            DeliveryOptions.Pickup | DeliveryOptions.Courier, DeliveryType.Courier));
    }

    [Fact]
    public void ShouldApplyLegacyOnlyUpdate_True_When_Legacy_Value_Is_A_Real_Change()
    {
        Assert.True(DeliveryOptionsMapper.ShouldApplyLegacyOnlyUpdate(DeliveryOptions.Courier, DeliveryType.Pickup));
        Assert.True(DeliveryOptionsMapper.ShouldApplyLegacyOnlyUpdate(DeliveryOptions.Pickup, DeliveryType.Courier));
    }

    [Fact]
    public void ShouldApplyLegacyOnlyUpdate_True_When_Current_Flags_Are_Null_Or_None()
    {
        Assert.True(DeliveryOptionsMapper.ShouldApplyLegacyOnlyUpdate(null, DeliveryType.Courier));
        Assert.True(DeliveryOptionsMapper.ShouldApplyLegacyOnlyUpdate(DeliveryOptions.None, DeliveryType.Pickup));
    }
}
