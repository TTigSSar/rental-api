using System.Text.Encodings.Web;
using System.Text.Json;
using RentalPlatform.Application.DTOs;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Infrastructure.Persistence;
using RentalPlatform.Infrastructure.Services;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Listings;

// The home point is a family's home address. ADR-008 says the exact coordinate is visible to the
// owner and admins ONLY, and everyone else gets the geohash-cell centroid instead — the home-point
// model does not change that rule, it just means there is now one exact pair per USER rather than
// per listing, which makes a leak worse: one slip exposes every listing that person has.
//
// Modelled on ListingOwnerPhoneNotExposedTests. Each test serialises the real response object and
// searches the JSON for the exact decimals, because that catches a leak under ANY property name —
// a re-add as "homeLatitude", "exactLat" or nested inside an owner object would all slip past a
// key-name check while leaking the same secret.
public sealed class HomePointCoordinatePrivacyTests
{
    private static readonly Guid OwnerId = new("f1000000-0000-0000-0000-000000000001");
    private static readonly Guid OtherUserId = new("f1000000-0000-0000-0000-000000000002");
    private static readonly Guid CategoryId = new("f1000000-0000-0000-0000-000000000003");
    private static readonly Guid ListingId = new("f1000000-0000-0000-0000-000000000004");

    // Deliberately unusual decimals: they cannot collide with an unrelated number in the payload,
    // so a substring hit means a real leak and not a coincidence. They sit inside Kentron.
    private const decimal ExactLatitude = 40.187431m;
    private const decimal ExactLongitude = 44.512877m;

    private static readonly JsonSerializerOptions SerializerOptions =
        new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static async Task SeedAsync(SqliteTestDatabase db)
    {
        await db.SeedAsync(
            TestData.User(OwnerId, "owner@test.local"),
            TestData.User(OtherUserId, "other@test.local"),
            TestData.Category(CategoryId));

        await db.SeedAsync(TestData.Listing(ListingId, OwnerId, CategoryId, ListingStatus.Approved));

        // Set the home point the way production does — through the single writer, so the listing
        // gets exactly the copy a real move would produce.
        await using var context = db.CreateContext();
        var service = new RentalPlatform.Application.Services.HomePointService(
            new HomePointStore(context),
            new GeohashSnapper(),
            new DistrictBoundaryProvider(),
            new FakeNotificationEmitter());

        var result = await service.SetHomePointAsync(OwnerId, ExactLatitude, ExactLongitude);
        Assert.True(result.IsSuccess);
    }

    private static void AssertNoExactCoordinates(string json)
    {
        Assert.DoesNotContain("40.187431", json, StringComparison.Ordinal);
        Assert.DoesNotContain("44.512877", json, StringComparison.Ordinal);
        // The field names themselves have no business on a public payload either — their presence
        // is the earliest warning that the private half of the model has been projected outward.
        Assert.DoesNotContain("homeLatitude", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("homeLongitude", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("homePoint", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Public_Profile_Never_Carries_The_Owners_Exact_Home_Coordinates()
    {
        using var db = new SqliteTestDatabase();
        await SeedAsync(db);

        await using var context = db.CreateContext();
        var service = new PublicUserProfileQueryService(context, new ReviewsStore(context));

        var profile = await service.GetPublicProfileAsync(OwnerId);
        Assert.NotNull(profile);
        AssertNoExactCoordinates(JsonSerializer.Serialize(profile, SerializerOptions));

        var listings = await service.GetUserListingsAsync(OwnerId);
        AssertNoExactCoordinates(JsonSerializer.Serialize(listings, SerializerOptions));
    }

    [Fact]
    public async Task Listing_Detail_Never_Carries_The_Exact_Home_Coordinates_For_An_Anonymous_Caller()
    {
        using var db = new SqliteTestDatabase();
        await SeedAsync(db);

        await using var context = db.CreateContext();
        var details = await new ListingsQueryService(context)
            .GetApprovedListingByIdAsync(ListingId, callerId: null, isAdmin: false);

        Assert.NotNull(details);
        AssertNoExactCoordinates(JsonSerializer.Serialize(details, SerializerOptions));
    }

    [Fact]
    public async Task Listing_Detail_Never_Carries_The_Exact_Home_Coordinates_For_Another_Signed_In_User()
    {
        using var db = new SqliteTestDatabase();
        await SeedAsync(db);

        await using var context = db.CreateContext();
        var details = await new ListingsQueryService(context)
            .GetApprovedListingByIdAsync(ListingId, OtherUserId, isAdmin: false);

        Assert.NotNull(details);
        AssertNoExactCoordinates(JsonSerializer.Serialize(details, SerializerOptions));
    }

    // Map pins are the highest-volume exposure of a coordinate in the product — one request returns
    // hundreds. ADR-008 requires the public pair here with no fallback to the exact one.
    [Fact]
    public async Task Map_Pins_Never_Carry_The_Exact_Home_Coordinates()
    {
        using var db = new SqliteTestDatabase();
        await SeedAsync(db);

        await using var context = db.CreateContext();
        var pins = await new ListingsQueryService(context).GetMapPinsAsync(new ListingsQueryFilter());

        Assert.NotEmpty(pins.Items);
        AssertNoExactCoordinates(JsonSerializer.Serialize(pins, SerializerOptions));
    }

    [Fact]
    public async Task Search_Results_Never_Carry_The_Exact_Home_Coordinates()
    {
        using var db = new SqliteTestDatabase();
        await SeedAsync(db);

        await using var context = db.CreateContext();
        var results = await new ListingsQueryService(context).GetApprovedListingsAsync(new ListingsQueryFilter());

        Assert.NotEmpty(results.Items);
        AssertNoExactCoordinates(JsonSerializer.Serialize(results, SerializerOptions));
    }

    // The counterpart, and the reason the tests above are not simply asserting "no coordinates
    // anywhere": the OWNER does get the exact pair back, on their own CurrentUserResponse and
    // nowhere else. Without this, a regression that blanked the home point everywhere would still
    // pass every test above.
    [Fact]
    public async Task The_Owners_Own_Current_User_Payload_Does_Carry_The_Exact_Coordinates()
    {
        using var db = new SqliteTestDatabase();
        await SeedAsync(db);

        await using var context = db.CreateContext();
        var homePoints = new RentalPlatform.Application.Services.HomePointService(
            new HomePointStore(context),
            new GeohashSnapper(),
            new DistrictBoundaryProvider(),
            new FakeNotificationEmitter());
        var service = new RentalPlatform.Application.Services.AuthService(
            new UserAuthStore(context),
            new BcryptPasswordHasher(),
            new FakeJwtTokenService(),
            new FakeCurrentUserContext(OwnerId),
            new FakeExternalIdentityTokenValidator(),
            homePoints,
            new RentalPlatform.Application.Services.EmailVerificationService(
                new EmailVerificationStore(context),
                new BcryptPasswordHasher(),
                homePoints,
                new FakeEmailService(),
                new FakeEmailVerificationSettings(),
                new FakeEmailSendBudget(),
                TimeProvider.System),
            new EmailVerificationStore(context),
            TimeProvider.System);

        var result = await service.GetCurrentUserAsync();

        Assert.True(result.IsSuccess);
        var json = JsonSerializer.Serialize(result.Value, SerializerOptions);
        Assert.Contains("40.187431", json, StringComparison.Ordinal);
        Assert.Contains("44.512877", json, StringComparison.Ordinal);
    }
}
