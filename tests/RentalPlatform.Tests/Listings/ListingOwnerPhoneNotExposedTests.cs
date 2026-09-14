using System.Text.Encodings.Web;
using System.Text.Json;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Infrastructure.Services;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Listings;

// The platform-mediated phone reveal (owner's phone number surfaced to a renter once their
// booking reached Approved) was removed entirely in favour of booking-scoped chat: a renter can
// only message an owner when a rental request exists, and can ask for the number there. This
// suite is a regression guard against a future accidental re-add of PhoneNumber onto
// ListingOwnerResponse / the listing-details payload.
//
// The pickup AddressLine gate that used to share this file's name and Setup is unaffected by this
// change and is already covered exhaustively (anonymous, unrelated user, pending booking, owner,
// admin, and Approved/Active/Completed reveal) by ListingDetailAddressRevealTests — not
// duplicated here.
public sealed class ListingOwnerPhoneNotExposedTests
{
    private static readonly Guid OwnerId = new("e0000000-0000-0000-0000-000000000001");
    private static readonly Guid RenterId = new("e0000000-0000-0000-0000-000000000002");
    private static readonly Guid CategoryId = new("e0000000-0000-0000-0000-000000000003");
    private static readonly Guid ListingId = new("e0000000-0000-0000-0000-000000000004");
    private const string OwnerPhone = "+374 99 123456";

    private static readonly DateOnly Today = TestData.Today;

    private static readonly JsonSerializerOptions SerializerOptions =
        new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

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

    [Theory]
    [InlineData(BookingStatus.Pending)]
    [InlineData(BookingStatus.Approved)]
    [InlineData(BookingStatus.Active)]
    [InlineData(BookingStatus.Completed)]
    public async Task Owner_Payload_Never_Carries_A_Phone_Number_Regardless_Of_Booking_Status(BookingStatus status)
    {
        using var db = new SqliteTestDatabase();
        await SeedAsync(db, status);

        await using var context = db.CreateContext();
        var result = await new ListingsQueryService(context)
            .GetApprovedListingByIdAsync(ListingId, RenterId, isAdmin: false);

        Assert.NotNull(result);
        var json = JsonSerializer.Serialize(result, SerializerOptions);
        // Both checks matter and neither substitutes for the other: the key check catches a
        // straightforward regression (PhoneNumber re-added to the DTO as-is), while the value
        // check catches a re-add under a different property name (e.g. "ownerContact" or
        // "contactNumber") that would still leak the same secret but slip past the key check.
        Assert.DoesNotContain("phoneNumber", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(OwnerPhone, json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Owner_Payload_Never_Carries_A_Phone_Number_For_Anonymous_Caller()
    {
        using var db = new SqliteTestDatabase();
        await SeedAsync(db, BookingStatus.Approved);

        await using var context = db.CreateContext();
        var result = await new ListingsQueryService(context)
            .GetApprovedListingByIdAsync(ListingId, callerId: null, isAdmin: false);

        Assert.NotNull(result);
        var json = JsonSerializer.Serialize(result, SerializerOptions);
        // See the comment in the parametrized test above: key absence and value absence guard
        // against different regressions and neither should be dropped as "redundant".
        Assert.DoesNotContain("phoneNumber", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(OwnerPhone, json, StringComparison.Ordinal);
    }
}
