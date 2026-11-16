using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Infrastructure.Services;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Categories;

// ADR-064: home section items carry rating/review count (catalogue rule: rating hidden below
// 2 reviews) and the listing's district.
public sealed class HomeSectionsPreviewTests
{
    private static readonly Guid OwnerId = new("f1000000-0000-0000-0000-000000000001");
    private static readonly Guid CategoryId = new("f1000000-0000-0000-0000-000000000002");
    private static readonly Guid RenterId = new("f1000000-0000-0000-0000-000000000003");
    private static readonly Guid TwoReviewsListingId = new("f1000000-0000-0000-0000-000000000010");
    private static readonly Guid OneReviewListingId = new("f1000000-0000-0000-0000-000000000011");
    private static readonly Guid NoReviewListingId = new("f1000000-0000-0000-0000-000000000012");

    private static ToyReview Review(int n, Guid listingId, int rating)
    {
        var bookingId = new Guid($"f1000000-0000-0000-0000-0000000001{n:00}");
        return new ToyReview
        {
            Id = new Guid($"f1000000-0000-0000-0000-0000000002{n:00}"),
            BookingId = bookingId,
            ListingId = listingId,
            ReviewerId = RenterId,
            OverallRating = rating,
            ConditionRating = rating,
            CleanlinessRating = rating,
            ValueForMoneyRating = rating,
            FunPlayValueRating = rating,
            DescriptionAccuracyRating = rating,
            CreatedAt = DateTime.UtcNow
        };
    }

    private static Booking BookingFor(int n, Guid listingId) =>
        TestData.Booking(
            new Guid($"f1000000-0000-0000-0000-0000000001{n:00}"),
            listingId,
            RenterId,
            TestData.Today.AddDays(-30 + n * 3),
            TestData.Today.AddDays(-29 + n * 3),
            BookingStatus.Completed);

    [Fact]
    public async Task Items_Carry_Rating_ReviewCount_And_District()
    {
        using var db = new SqliteTestDatabase();
        var withDistrict = TestData.Listing(TwoReviewsListingId, OwnerId, CategoryId);
        withDistrict.DistrictId = TestData.KentronDistrictId;
        var oneReview = TestData.Listing(OneReviewListingId, OwnerId, CategoryId);
        var noReview = TestData.Listing(NoReviewListingId, OwnerId, CategoryId);
        await db.SeedAsync(
            TestData.User(OwnerId, "owner@test.local"),
            TestData.User(RenterId, "renter@test.local"),
            TestData.Category(CategoryId),
            withDistrict, oneReview, noReview,
            BookingFor(1, TwoReviewsListingId),
            BookingFor(2, TwoReviewsListingId),
            BookingFor(3, OneReviewListingId));
        await db.SeedAsync(
            Review(1, TwoReviewsListingId, 5),
            Review(2, TwoReviewsListingId, 4),
            Review(3, OneReviewListingId, 5));

        await using var context = db.CreateContext();
        var sections = await new HomeSectionsQueryService(context).GetSectionsAsync(itemsPerSection: 12);
        var items = sections.Sections.Single(s => s.Key == "recent").Items.ToDictionary(i => i.Id);

        // (a) 2+ reviews: rounded average and count.
        Assert.Equal(2, items[TwoReviewsListingId].ReviewCount);
        Assert.Equal(4.5, items[TwoReviewsListingId].Rating);
        // (b) 1 review: aggregate hidden, count still reported.
        Assert.Equal(1, items[OneReviewListingId].ReviewCount);
        Assert.Null(items[OneReviewListingId].Rating);
        Assert.Equal(0, items[NoReviewListingId].ReviewCount);
        Assert.Null(items[NoReviewListingId].Rating);
        // (c) district filled when present, null otherwise.
        var district = items[TwoReviewsListingId].District;
        Assert.NotNull(district);
        Assert.Equal(TestData.KentronDistrictId, district!.Id);
        Assert.False(string.IsNullOrEmpty(district.NameEn));
        Assert.Null(items[OneReviewListingId].District);
    }
}
