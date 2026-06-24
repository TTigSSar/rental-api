using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Infrastructure.Services;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Listings;

// The owner's phone number must only be revealed to a renter once their booking has reached
// at least Approved — a Pending request must not leak contact details.
public sealed class ListingDetailContactRevealTests
{
    private static readonly Guid OwnerId = new("e0000000-0000-0000-0000-000000000001");
    private static readonly Guid RenterId = new("e0000000-0000-0000-0000-000000000002");
    private static readonly Guid CategoryId = new("e0000000-0000-0000-0000-000000000003");
    private static readonly Guid ListingId = new("e0000000-0000-0000-0000-000000000004");
    private const string OwnerPhone = "+374 99 123456";

    private static readonly DateOnly Today = TestData.Today;

    private static async Task SeedAsync(SqliteTestDatabase db, BookingStatus renterBookingStatus)
    {
        var owner = TestData.User(OwnerId, "owner@test.local");
        owner.PhoneNumber = OwnerPhone;

        await db.SeedAsync(
            owner,
            TestData.User(RenterId, "renter@test.local"),
            TestData.Category(CategoryId));

        await db.SeedAsync(TestData.Listing(ListingId, OwnerId, CategoryId, ListingStatus.Approved));

        await db.SeedAsync(TestData.Booking(
            Guid.NewGuid(), ListingId, RenterId,
            Today.AddDays(5), Today.AddDays(7),
            renterBookingStatus,
            expiresAt: DateTime.UtcNow.AddHours(24)));
    }

    [Fact]
    public async Task Phone_Hidden_While_Booking_Is_Pending()
    {
        using var db = new SqliteTestDatabase();
        await SeedAsync(db, BookingStatus.Pending);

        await using var context = db.CreateContext();
        var result = await new ListingsQueryService(context)
            .GetApprovedListingByIdAsync(ListingId, RenterId, isAdmin: false);

        Assert.NotNull(result);
        Assert.Null(result!.Owner.PhoneNumber);
    }

    [Theory]
    [InlineData(BookingStatus.Approved)]
    [InlineData(BookingStatus.Active)]
    [InlineData(BookingStatus.Completed)]
    public async Task Phone_Revealed_Once_Booking_Reaches_Approved(BookingStatus status)
    {
        using var db = new SqliteTestDatabase();
        await SeedAsync(db, status);

        await using var context = db.CreateContext();
        var result = await new ListingsQueryService(context)
            .GetApprovedListingByIdAsync(ListingId, RenterId, isAdmin: false);

        Assert.NotNull(result);
        Assert.Equal(OwnerPhone, result!.Owner.PhoneNumber);
    }

    [Fact]
    public async Task Phone_Hidden_For_Anonymous_Caller()
    {
        using var db = new SqliteTestDatabase();
        await SeedAsync(db, BookingStatus.Approved);

        await using var context = db.CreateContext();
        var result = await new ListingsQueryService(context)
            .GetApprovedListingByIdAsync(ListingId, callerId: null, isAdmin: false);

        Assert.NotNull(result);
        Assert.Null(result!.Owner.PhoneNumber);
    }
}
