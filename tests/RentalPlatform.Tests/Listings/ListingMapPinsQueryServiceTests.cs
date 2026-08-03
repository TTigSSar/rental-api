using RentalPlatform.Application.DTOs;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Infrastructure.Services;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Listings;

// Maps P2-1: GET /api/listings/map-pins. Shares the ListingsQueryServiceFilterTests filter
// predicate (covered there) — these tests focus on what's specific to the pins endpoint: the
// 500-pin cap/IsTruncated flag, exclusion of listings with no public coordinate, and — the one
// that matters most per ADR-008 — that a pin never carries the exact coordinate.
public sealed class ListingMapPinsQueryServiceTests
{
    private static readonly Guid OwnerId = new("b0000000-0000-0000-0000-000000000001");
    private static readonly Guid CategoryId = new("b0000000-0000-0000-0000-000000000002");

    private static async Task<SqliteTestDatabase> SeedBaseAsync()
    {
        var db = new SqliteTestDatabase();
        await db.SeedAsync(
            TestData.User(OwnerId, "owner@test.local"),
            TestData.Category(CategoryId));
        return db;
    }

    private static Listing Build(Guid id, string title = "Toy") =>
        TestData.Listing(id, OwnerId, CategoryId);

    [Fact]
    public async Task Pin_Exposes_Public_Coordinate_Not_Exact_Coordinate()
    {
        // The security property from ADR-008: a map pin must publish the geohash-cell-centroid
        // (PublicLatitude/PublicLongitude), never the exact pin the owner dropped
        // (Latitude/Longitude) — even though both are populated here, deliberately different.
        using var db = await SeedBaseAsync();
        var id = new Guid("b0000000-0000-0000-0000-000000000010");
        var listing = Build(id);
        listing.Latitude = 40.123456m;
        listing.Longitude = 44.654321m;
        listing.PublicLatitude = 40.19m;
        listing.PublicLongitude = 44.52m;
        await db.SeedAsync(listing);

        await using var context = db.CreateContext();
        var result = await new ListingsQueryService(context).GetMapPinsAsync(new ListingsQueryFilter());

        var pin = Assert.Single(result.Items);
        Assert.Equal(listing.PublicLatitude.Value, pin.Latitude);
        Assert.Equal(listing.PublicLongitude.Value, pin.Longitude);
        Assert.NotEqual(listing.Latitude.Value, pin.Latitude);
        Assert.NotEqual(listing.Longitude.Value, pin.Longitude);
    }

    [Fact]
    public async Task Excludes_Listing_With_No_Public_Coordinate()
    {
        using var db = await SeedBaseAsync();
        var id = new Guid("b0000000-0000-0000-0000-000000000011");
        var listing = Build(id);
        listing.Latitude = 40.123456m;
        listing.Longitude = 44.654321m;
        listing.PublicLatitude = null;
        listing.PublicLongitude = null;
        await db.SeedAsync(listing);

        await using var context = db.CreateContext();
        var result = await new ListingsQueryService(context).GetMapPinsAsync(new ListingsQueryFilter());

        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task Excludes_NonApproved_Listing()
    {
        using var db = await SeedBaseAsync();
        var id = new Guid("b0000000-0000-0000-0000-000000000012");
        var listing = TestData.Listing(id, OwnerId, CategoryId, ListingStatus.PendingApproval);
        listing.PublicLatitude = 40.19m;
        listing.PublicLongitude = 44.52m;
        await db.SeedAsync(listing);

        await using var context = db.CreateContext();
        var result = await new ListingsQueryService(context).GetMapPinsAsync(new ListingsQueryFilter());

        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task Returns_All_Pins_And_Not_Truncated_When_At_Or_Below_Cap()
    {
        using var db = await SeedBaseAsync();
        var listings = Enumerable.Range(0, 500)
            .Select(i =>
            {
                var listing = Build(new Guid($"b0000000-0001-0000-0000-{i:D12}"));
                listing.PublicLatitude = 40.0m + i * 0.0001m;
                listing.PublicLongitude = 44.0m + i * 0.0001m;
                return listing;
            })
            .ToArray();
        await db.SeedAsync(listings.Cast<object>().ToArray());

        await using var context = db.CreateContext();
        var result = await new ListingsQueryService(context).GetMapPinsAsync(new ListingsQueryFilter());

        Assert.Equal(500, result.Items.Count);
        Assert.False(result.IsTruncated);
    }

    [Fact]
    public async Task Truncates_At_500_Pins_And_Reports_IsTruncated()
    {
        using var db = await SeedBaseAsync();
        var listings = Enumerable.Range(0, 501)
            .Select(i =>
            {
                var listing = Build(new Guid($"b0000000-0002-0000-0000-{i:D12}"));
                listing.PublicLatitude = 40.0m + i * 0.0001m;
                listing.PublicLongitude = 44.0m + i * 0.0001m;
                return listing;
            })
            .ToArray();
        await db.SeedAsync(listings.Cast<object>().ToArray());

        await using var context = db.CreateContext();
        var result = await new ListingsQueryService(context).GetMapPinsAsync(new ListingsQueryFilter());

        Assert.Equal(500, result.Items.Count);
        Assert.True(result.IsTruncated);
    }

    [Fact]
    public async Task Pin_Reports_Average_Rating_And_Review_Count_When_Reviews_Exist()
    {
        // Same threshold/rounding as ListingPreviewResponse.Rating in GetApprovedListingsAsync:
        // the aggregate is exposed once a listing has at least 2 reviews.
        using var db = await SeedBaseAsync();
        var renterId = new Guid("b0000000-0000-0000-0000-000000000003");
        var strangerId = new Guid("b0000000-0000-0000-0000-000000000004");
        await db.SeedAsync(
            TestData.User(renterId, "renter-rating@test.local"),
            TestData.User(strangerId, "stranger-rating@test.local"));

        var id = new Guid("b0000000-0000-0000-0000-000000000013");
        var listing = Build(id);
        listing.PublicLatitude = 40.19m;
        listing.PublicLongitude = 44.52m;
        await db.SeedAsync(listing);

        var pastStart = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-10));
        var pastEnd = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-3));
        var bookingOneId = Guid.NewGuid();
        var bookingTwoId = Guid.NewGuid();
        await db.SeedAsync(
            TestData.Booking(bookingOneId, id, renterId, pastStart, pastEnd, BookingStatus.Completed),
            TestData.Booking(bookingTwoId, id, strangerId, pastStart, pastEnd, BookingStatus.Completed));

        await db.SeedAsync(
            new ToyReview
            {
                Id = Guid.NewGuid(),
                BookingId = bookingOneId,
                ListingId = id,
                ReviewerId = renterId,
                OverallRating = 5,
                ConditionRating = 5,
                CleanlinessRating = 5,
                ValueForMoneyRating = 5,
                FunPlayValueRating = 5,
                DescriptionAccuracyRating = 5,
                CreatedAt = DateTime.UtcNow
            },
            new ToyReview
            {
                Id = Guid.NewGuid(),
                BookingId = bookingTwoId,
                ListingId = id,
                ReviewerId = strangerId,
                OverallRating = 4,
                ConditionRating = 4,
                CleanlinessRating = 4,
                ValueForMoneyRating = 4,
                FunPlayValueRating = 4,
                DescriptionAccuracyRating = 4,
                CreatedAt = DateTime.UtcNow
            });

        await using var context = db.CreateContext();
        var result = await new ListingsQueryService(context).GetMapPinsAsync(new ListingsQueryFilter());

        var pin = Assert.Single(result.Items);
        Assert.Equal(2, pin.ReviewCount);
        Assert.Equal(4.5m, pin.Rating);
    }

    [Fact]
    public async Task Pin_Has_Null_Rating_And_Zero_ReviewCount_When_No_Reviews()
    {
        using var db = await SeedBaseAsync();
        var id = new Guid("b0000000-0000-0000-0000-000000000014");
        var listing = Build(id);
        listing.PublicLatitude = 40.19m;
        listing.PublicLongitude = 44.52m;
        await db.SeedAsync(listing);

        await using var context = db.CreateContext();
        var result = await new ListingsQueryService(context).GetMapPinsAsync(new ListingsQueryFilter());

        var pin = Assert.Single(result.Items);
        Assert.Equal(0, pin.ReviewCount);
        Assert.Null(pin.Rating);
    }
}
