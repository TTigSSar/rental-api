using Microsoft.Extensions.Logging.Abstractions;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Infrastructure.Persistence;
using RentalPlatform.Infrastructure.Services;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Infrastructure;

// The "pickup area changed" notification, which an owner's home-point move sends to the renter of
// every booking still in flight.
//
// Notification rows store finished strings, not translation keys (see Notification.cs), so the
// language is chosen HERE, at emit time, from the recipient's PreferredLanguage. That makes this
// the only place the three translations can be checked at all — nothing downstream can re-render
// them.
//
// The null-district variant matters more than it looks: a home point outside Yerevan can no longer
// be SAVED, but rows that predate that rule still exist (the AddUserHomePoint migration derived
// home points from listings without applying it — M-038), and when such an owner is reconciled the
// emitter is handed a null district. Without its own wording the sentence would have a hole where
// the district name goes, so it is covered here in all three languages even though no new write can
// produce it.
public sealed class PickupAreaChangedCopyTests
{
    private static readonly Guid OwnerId = new("e7000000-0000-0000-0000-000000000001");
    private static readonly Guid RenterId = new("e7000000-0000-0000-0000-000000000002");
    private static readonly Guid CategoryId = new("e7000000-0000-0000-0000-000000000003");
    private static readonly Guid ListingId = new("e7000000-0000-0000-0000-000000000004");
    private static readonly Guid PrimaryImageId = new("e7000000-0000-0000-0000-000000000005");
    private static readonly Guid SecondaryImageId = new("e7000000-0000-0000-0000-000000000006");

    private static District Kentron() => new()
    {
        Id = TestData.KentronDistrictId,
        Code = "kentron",
        NameEn = "Kentron",
        NameHy = "Կենտրոն",
        NameRu = "Кентрон"
    };

    private static async Task<Notification> EmitAsync(
        SqliteTestDatabase db,
        string? preferredLanguage,
        District? newDistrict)
    {
        // Only the recipient needs to exist, for the Notification FK.
        var renter = TestData.User(RenterId, "renter@test.local", preferredLanguage: preferredLanguage);
        await db.SeedAsync(renter);

        var owner = TestData.User(OwnerId, "owner@test.local", firstName: "Anahit", lastName: "Grigoryan");
        var listing = TestData.Listing(ListingId, OwnerId, CategoryId);
        listing.Title = "Wooden Train Set";
        // The toy's photos, in the shape every other emit path hands over: a non-primary image
        // first, so "the primary one wins" is an assertion about the ordering rule and not about
        // whichever image happens to be first in the collection.
        listing.Images = new List<ListingImage>
        {
            TestData.Image(SecondaryImageId, ListingId, isPrimary: false, sortOrder: 1),
            TestData.Image(PrimaryImageId, ListingId, isPrimary: true, sortOrder: 0)
        };

        var booking = TestData.Booking(
            Guid.NewGuid(), ListingId, RenterId,
            TestData.Today, TestData.Today.AddDays(2),
            BookingStatus.Approved);
        booking.Listing = listing;
        booking.Renter = renter;

        await using var context = db.CreateContext();
        var emitter = new NotificationEmitter(new NotificationsStore(context), NullLogger<NotificationEmitter>.Instance);

        await emitter.PickupAreaChangedAsync(booking, owner, newDistrict);

        return Assert.Single(context.Notifications);
    }

    [Fact]
    public async Task Emits_To_The_Renter_As_A_Booking_Pickup_Notification()
    {
        using var db = new SqliteTestDatabase();

        var notification = await EmitAsync(db, "en", Kentron());

        Assert.Equal(RenterId, notification.RecipientId);
        Assert.Equal(NotificationKind.Pickup, notification.Kind);
        Assert.Equal(NotificationCategory.Booking, notification.Category);
        Assert.Equal(NotificationEntityType.Booking, notification.EntityType);
        Assert.StartsWith("/bookings/", notification.DeepLink, StringComparison.Ordinal);
        Assert.Equal("Wooden Train Set", notification.ToyTitle);
        Assert.False(notification.Urgent);

        // The thumbnail the notification list renders. It was null for every pickup notification
        // ever emitted — not because of anything here, but because the query feeding this emit path
        // (HomePointStore.GetInFlightBookingsByOwnerAsync) included the Listing without its Images,
        // and with lazy loading off an un-included collection is simply empty. This assertion pins
        // the emitter's half; HomePointServiceTests pins that the store actually loads them.
        Assert.Equal(
            TestData.Image(PrimaryImageId, ListingId, isPrimary: true, sortOrder: 0).Url,
            notification.ToyImageUrl);
    }

    [Theory]
    [InlineData("en", "Kentron")]
    [InlineData("hy", "Կենտրոն")]
    [InlineData("ru", "Кентрон")]
    public async Task Names_The_New_District_In_The_Recipients_Language(string language, string expectedDistrictName)
    {
        using var db = new SqliteTestDatabase();

        var notification = await EmitAsync(db, language, Kentron());

        Assert.Contains(expectedDistrictName, notification.Body, StringComparison.Ordinal);
        Assert.Contains("Wooden Train Set", notification.Body, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(notification.Title));
        Assert.False(string.IsNullOrWhiteSpace(notification.PrimaryActionLabel));
    }

    // The legacy path: no district to name. Every language must still produce a complete sentence,
    // with no empty slot and no stray punctuation where the name would have been.
    [Theory]
    [InlineData("en")]
    [InlineData("hy")]
    [InlineData("ru")]
    public async Task Has_Its_Own_Wording_When_There_Is_No_District(string language)
    {
        using var db = new SqliteTestDatabase();

        var notification = await EmitAsync(db, language, newDistrict: null);

        Assert.False(string.IsNullOrWhiteSpace(notification.Body));
        Assert.Contains("Wooden Train Set", notification.Body, StringComparison.Ordinal);
        // No district name leaked in from the with-district variant.
        Assert.DoesNotContain("Kentron", notification.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Կենտրոն", notification.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("Кентрон", notification.Body, StringComparison.Ordinal);
    }

    // The with-district and without-district bodies must actually differ — if the null variant
    // silently fell back to the district wording, the assertions above would still pass while the
    // renter read a sentence about a district that does not exist.
    [Fact]
    public async Task The_Null_District_Wording_Differs_From_The_District_Wording()
    {
        string withDistrict, withoutDistrict;

        using (var db = new SqliteTestDatabase())
        {
            withDistrict = (await EmitAsync(db, "en", Kentron())).Body;
        }

        using (var db = new SqliteTestDatabase())
        {
            withoutDistrict = (await EmitAsync(db, "en", newDistrict: null)).Body;
        }

        Assert.NotEqual(withDistrict, withoutDistrict);
    }

    // Unknown, blank and regional-variant language tags all have to land somewhere sensible rather
    // than throwing or producing an empty body.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("de")]
    [InlineData("ru-RU")]
    public async Task Falls_Back_To_A_Complete_Body_For_Any_Language_Tag(string? language)
    {
        using var db = new SqliteTestDatabase();

        var notification = await EmitAsync(db, language, Kentron());

        Assert.False(string.IsNullOrWhiteSpace(notification.Title));
        Assert.False(string.IsNullOrWhiteSpace(notification.Body));
    }
}
