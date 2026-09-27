using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Infrastructure.Services;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Listings;

// ListingsQueryService.GetApprovedListingByIdAsync builds ListingDetailsResponse entirely inside
// one EF query; DeliveryTypes can't be expanded there (the flag-expansion loop in
// DeliveryOptionsMapper.Expand isn't SQL-translatable), so it's filled in after materialization —
// see the row.Dto.DeliveryTypes assignment in that method. These tests pin both "it doesn't throw
// a client-eval exception" and the expansion/fallback semantics for this specific read path.
public sealed class ListingDetailDeliveryOptionsTests
{
    private static readonly Guid OwnerId = new("d0000000-0000-0000-0000-000000000001");
    private static readonly Guid CategoryId = new("d0000000-0000-0000-0000-000000000002");
    private static readonly Guid ListingId = new("d0000000-0000-0000-0000-000000000003");

    private static async Task SeedAsync(SqliteTestDatabase db, Action<Listing> configure)
    {
        await db.SeedAsync(
            TestData.User(OwnerId, "owner@test.local"),
            TestData.Category(CategoryId));

        var listing = TestData.Listing(ListingId, OwnerId, CategoryId, ListingStatus.Approved);
        configure(listing);
        await db.SeedAsync(listing);
    }

    [Fact]
    public async Task Returns_Both_Options_In_Pickup_Then_Courier_Order()
    {
        using var db = new SqliteTestDatabase();
        await SeedAsync(db, listing =>
        {
            listing.DeliveryOptions = DeliveryOptions.Courier | DeliveryOptions.Pickup;
            listing.DeliveryType = DeliveryType.Pickup;
        });

        await using var context = db.CreateContext();
        var result = await new ListingsQueryService(context).GetApprovedListingByIdAsync(ListingId);

        Assert.NotNull(result);
        Assert.Equal(new[] { DeliveryType.Pickup, DeliveryType.Courier }, result!.DeliveryTypes);
        Assert.Equal(DeliveryType.Pickup, result.DeliveryType);
    }

    [Fact]
    public async Task Falls_Back_To_Legacy_Single_Value_For_Pre_Migration_Rows()
    {
        using var db = new SqliteTestDatabase();
        await SeedAsync(db, listing =>
        {
            // Pre-migration row: legacy DeliveryType set, DeliveryOptions never backfilled.
            listing.DeliveryType = DeliveryType.Courier;
            listing.DeliveryOptions = null;
        });

        await using var context = db.CreateContext();
        var result = await new ListingsQueryService(context).GetApprovedListingByIdAsync(ListingId);

        Assert.NotNull(result);
        Assert.Equal(new[] { DeliveryType.Courier }, result!.DeliveryTypes);
    }

    [Fact]
    public async Task Returns_Null_When_Neither_Is_Set()
    {
        using var db = new SqliteTestDatabase();
        await SeedAsync(db, _ => { });

        await using var context = db.CreateContext();
        var result = await new ListingsQueryService(context).GetApprovedListingByIdAsync(ListingId);

        Assert.NotNull(result);
        Assert.Null(result!.DeliveryTypes);
    }
}
