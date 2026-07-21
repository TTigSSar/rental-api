using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Infrastructure.Services;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Listings;

// The pickup AddressLine must follow the same contact-reveal gate as the owner's phone number
// (ListingDetailContactRevealTests): owner and admin always, plus a renter whose booking reached
// at least Approved. Everyone else — including anonymous callers and unrelated authenticated
// users — must get null. This closes a leak where AddressLine was returned ungated while only
// Latitude/Longitude were gated behind CanSeeExactCoordinates, defeating the approximate-location
// feature (a caller could read the exact street address straight from the same response).
public sealed class ListingDetailAddressRevealTests
{
    private static readonly Guid OwnerId = new("a0000000-0000-0000-0000-000000000001");
    private static readonly Guid RenterId = new("a0000000-0000-0000-0000-000000000002");
    private static readonly Guid OtherUserId = new("a0000000-0000-0000-0000-000000000003");
    private static readonly Guid AdminId = new("a0000000-0000-0000-0000-000000000004");
    private static readonly Guid CategoryId = new("a0000000-0000-0000-0000-000000000005");
    private static readonly Guid ListingId = new("a0000000-0000-0000-0000-000000000006");
    private const string StreetAddress = "12 Mashtots Ave, Apt 5";

    private static readonly DateOnly Today = TestData.Today;

    private static async Task SeedAsync(SqliteTestDatabase db, BookingStatus? renterBookingStatus)
    {
        await db.SeedAsync(
            TestData.User(OwnerId, "owner@test.local"),
            TestData.User(RenterId, "renter@test.local"),
            TestData.User(OtherUserId, "other@test.local"),
            TestData.User(AdminId, "admin@test.local", role: UserRole.Admin),
            TestData.Category(CategoryId));

        var listing = TestData.Listing(ListingId, OwnerId, CategoryId, ListingStatus.Approved);
        listing.AddressLine = StreetAddress;
        await db.SeedAsync(listing);

        if (renterBookingStatus.HasValue)
        {
            await db.SeedAsync(TestData.Booking(
                Guid.NewGuid(), ListingId, RenterId,
                Today.AddDays(5), Today.AddDays(7),
                renterBookingStatus.Value,
                expiresAt: DateTime.UtcNow.AddHours(24)));
        }
    }

    [Fact]
    public async Task Address_Hidden_For_Anonymous_Caller()
    {
        using var db = new SqliteTestDatabase();
        await SeedAsync(db, renterBookingStatus: null);

        await using var context = db.CreateContext();
        var result = await new ListingsQueryService(context)
            .GetApprovedListingByIdAsync(ListingId, callerId: null, isAdmin: false);

        Assert.NotNull(result);
        Assert.Null(result!.AddressLine);
    }

    [Fact]
    public async Task Address_Hidden_For_Unrelated_Authenticated_Caller()
    {
        using var db = new SqliteTestDatabase();
        await SeedAsync(db, renterBookingStatus: null);

        await using var context = db.CreateContext();
        var result = await new ListingsQueryService(context)
            .GetApprovedListingByIdAsync(ListingId, callerId: OtherUserId, isAdmin: false);

        Assert.NotNull(result);
        Assert.Null(result!.AddressLine);
    }

    [Fact]
    public async Task Address_Hidden_While_Renter_Booking_Is_Pending()
    {
        using var db = new SqliteTestDatabase();
        await SeedAsync(db, BookingStatus.Pending);

        await using var context = db.CreateContext();
        var result = await new ListingsQueryService(context)
            .GetApprovedListingByIdAsync(ListingId, callerId: RenterId, isAdmin: false);

        Assert.NotNull(result);
        Assert.Null(result!.AddressLine);
    }

    [Theory]
    [InlineData(BookingStatus.Approved)]
    [InlineData(BookingStatus.Active)]
    [InlineData(BookingStatus.Completed)]
    public async Task Address_Revealed_For_Renter_Once_Booking_Reaches_Approved(BookingStatus status)
    {
        using var db = new SqliteTestDatabase();
        await SeedAsync(db, status);

        await using var context = db.CreateContext();
        var result = await new ListingsQueryService(context)
            .GetApprovedListingByIdAsync(ListingId, callerId: RenterId, isAdmin: false);

        Assert.NotNull(result);
        Assert.Equal(StreetAddress, result!.AddressLine);
    }

    [Fact]
    public async Task Address_Revealed_For_Owner()
    {
        using var db = new SqliteTestDatabase();
        await SeedAsync(db, renterBookingStatus: null);

        await using var context = db.CreateContext();
        var result = await new ListingsQueryService(context)
            .GetApprovedListingByIdAsync(ListingId, callerId: OwnerId, isAdmin: false);

        Assert.NotNull(result);
        Assert.Equal(StreetAddress, result!.AddressLine);
    }

    [Fact]
    public async Task Address_Revealed_For_Admin()
    {
        using var db = new SqliteTestDatabase();
        await SeedAsync(db, renterBookingStatus: null);

        await using var context = db.CreateContext();
        var result = await new ListingsQueryService(context)
            .GetApprovedListingByIdAsync(ListingId, callerId: AdminId, isAdmin: true);

        Assert.NotNull(result);
        Assert.Equal(StreetAddress, result!.AddressLine);
    }
}
