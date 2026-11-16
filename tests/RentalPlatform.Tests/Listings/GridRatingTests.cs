using RentalPlatform.Application.Services;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Infrastructure.Persistence;
using RentalPlatform.Infrastructure.Services;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Listings;

// Catalogue-shaped grids (favorites, public-profile listings) share the catalogue's rating rule:
// the aggregate only from 2 reviews on, the count always real (ADR-064 §2, ADR-014).
public sealed class GridRatingTests
{
    private static readonly Guid OwnerId = new("c0000000-0000-0000-0000-000000000001");
    private static readonly Guid RenterId = new("c0000000-0000-0000-0000-000000000002");
    private static readonly Guid CategoryId = new("c0000000-0000-0000-0000-000000000003");
    private static readonly Guid TwoId = new("c0000000-0000-0000-0000-000000000011");
    private static readonly Guid OneId = new("c0000000-0000-0000-0000-000000000012");
    private static readonly Guid NoneId = new("c0000000-0000-0000-0000-000000000013");

    private static async Task<SqliteTestDatabase> SeedAsync()
    {
        var db = new SqliteTestDatabase();
        var two = TestData.Listing(TwoId, OwnerId, CategoryId);
        two.DistrictId = TestData.KentronDistrictId;
        var bookings = new List<Booking>();
        var reviews = new List<ToyReview>();
        void Review(Guid listingId, int rating)
        {
            var bookingId = Guid.NewGuid();
            var offset = bookings.Count * 2;
            bookings.Add(TestData.Booking(bookingId, listingId, RenterId,
                TestData.Today.AddDays(-40 + offset), TestData.Today.AddDays(-39 + offset), BookingStatus.Completed));
            reviews.Add(new ToyReview
            {
                Id = Guid.NewGuid(), BookingId = bookingId, ListingId = listingId, ReviewerId = RenterId,
                OverallRating = rating, ConditionRating = rating, CleanlinessRating = rating,
                ValueForMoneyRating = rating, FunPlayValueRating = rating, DescriptionAccuracyRating = rating,
                CreatedAt = DateTime.UtcNow
            });
        }

        await db.SeedAsync(
            TestData.User(OwnerId, "owner@test.local"),
            TestData.User(RenterId, "renter@test.local"),
            TestData.Category(CategoryId),
            two, TestData.Listing(OneId, OwnerId, CategoryId), TestData.Listing(NoneId, OwnerId, CategoryId));
        Review(TwoId, 5); Review(TwoId, 4); Review(OneId, 5);
        await db.SeedAsync(bookings.Cast<object>().ToArray());
        await db.SeedAsync(reviews.Cast<object>().ToArray());
        await db.SeedAsync(
            new Favorite { Id = Guid.NewGuid(), UserId = RenterId, ListingId = TwoId, CreatedAt = DateTime.UtcNow },
            new Favorite { Id = Guid.NewGuid(), UserId = RenterId, ListingId = OneId, CreatedAt = DateTime.UtcNow },
            new Favorite { Id = Guid.NewGuid(), UserId = RenterId, ListingId = NoneId, CreatedAt = DateTime.UtcNow });
        return db;
    }

    [Fact]
    public async Task Favorites_Carry_Rating_Only_From_Two_Reviews_And_The_Real_Count_And_District()
    {
        using var db = await SeedAsync();
        await using var context = db.CreateContext();
        var service = new FavoritesService(new FakeCurrentUserContext(RenterId), new FavoritesStore(context));

        var result = await service.GetMineAsync();

        var items = result.Value!.ToDictionary(i => i.Id);
        Assert.Equal(2, items[TwoId].ReviewCount);
        Assert.Equal(4.5, items[TwoId].Rating);
        Assert.Equal(1, items[OneId].ReviewCount);
        Assert.Null(items[OneId].Rating);
        Assert.Equal(0, items[NoneId].ReviewCount);
        Assert.Null(items[NoneId].Rating);
        Assert.Equal(TestData.KentronDistrictId, items[TwoId].District!.Id);
    }

    [Fact]
    public async Task Public_Profile_Listings_Carry_Rating_Only_From_Two_Reviews_And_The_Real_Count()
    {
        using var db = await SeedAsync();
        await using var context = db.CreateContext();
        var service = new PublicUserProfileQueryService(context, new ReviewsStore(context));

        var result = await service.GetUserListingsAsync(OwnerId);

        var items = result!.ToDictionary(i => i.Id);
        Assert.Equal(2, items[TwoId].ReviewCount);
        Assert.Equal(4.5, items[TwoId].Rating);
        Assert.Equal(1, items[OneId].ReviewCount);
        Assert.Null(items[OneId].Rating);
        Assert.Equal(0, items[NoneId].ReviewCount);
        Assert.Null(items[NoneId].Rating);
        Assert.Equal(TestData.KentronDistrictId, items[TwoId].District!.Id);
    }
}
