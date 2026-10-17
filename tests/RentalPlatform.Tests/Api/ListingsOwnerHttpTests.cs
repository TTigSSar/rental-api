using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Infrastructure.Persistence;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Api;

// HTTP-level regression pin for the stale-client delivery-options bug (see
// DeliveryOptionsMapper.ShouldApplyLegacyOnlyUpdate and ListingsOwnerServiceTests).
//
// ListingsOwnerServiceTests already covers ShouldApplyLegacyOnlyUpdate against
// UpdateListingRequest objects built directly in C#. That never exercises System.Text.Json
// deserialization of the PATCH body, and the API registers a JsonStringEnumConverter
// (ServiceCollectionExtensions.AddApiServices) — a real, separate pipeline the stale UI's
// exact wire payload goes through. This test pins that a PATCH body carrying ONLY the legacy
// "deliveryType" string field (the omitted-key shape a cached pre-deploy client actually sends,
// not a C#-constructed DTO) still round-trips through real JSON model binding into a no-op that
// preserves a richer DeliveryOptions flag set.
[Collection("Integration")]
public sealed class ListingsOwnerHttpTests
{
    private readonly RentalPlatformWebAppFactory _factory;

    public ListingsOwnerHttpTests(RentalPlatformWebAppFactory factory) => _factory = factory;

    private HttpClient ClientFor(Guid userId, string email)
    {
        var token = TestJwtTokenHelper.GenerateToken(userId, email);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    // ---- Home point and the create gate ---------------------------------------------------------

    // 409, not 400: the body is perfectly well-formed and no field on it can fix the problem. The
    // owner has to go and set a home point first, and the client has to be able to tell that apart
    // from a validation failure to show the right screen.
    [Fact]
    public async Task Post_Listing_Returns_409_When_The_Owner_Has_No_Home_Point()
    {
        var ownerId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var email = $"{ownerId:N}@listing-owner.local";

        await _factory.SeedAsync(TestData.User(ownerId, email), TestData.Category(categoryId));

        var response = await ClientFor(ownerId, email).PostAsJsonAsync("/api/listings", new
        {
            categoryId,
            title = "Wooden Train Set",
            description = "A long enough description to satisfy validation rules.",
            pricePerDay = 2500,
            compensationAmount = 12000
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("listing.home_point_required", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    // M-036, applied to location: latitude, longitude, districtId, city and country came OFF the
    // create contract, and a stale client will keep sending them. Unknown JSON members are ignored,
    // so they bind to nothing — but "they bind to nothing" is exactly the kind of assumption that
    // silently stops being true. If any of them ever became writable again, an owner on a cached
    // bundle could publish a listing whose pin contradicts their home point, which is the failure
    // this whole feature exists to prevent.
    [Fact]
    public async Task Post_Listing_Ignores_Location_Fields_A_Stale_Client_Still_Sends()
    {
        var ownerId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var email = $"{ownerId:N}@listing-owner.local";

        await _factory.SeedAsync(
            TestData.OwnerWithHome(ownerId, email, TestData.KentronPoint),
            TestData.Category(categoryId));

        // Raw wire shape of a pre-deploy create wizard: the five removed fields, all populated with
        // values that are nowhere near the owner's home point.
        var response = await ClientFor(ownerId, email).PostAsJsonAsync("/api/listings", new
        {
            categoryId,
            title = "Wooden Train Set",
            description = "A long enough description to satisfy validation rules.",
            pricePerDay = 2500,
            compensationAmount = 12000,
            latitude = 12.3456,
            longitude = 65.4321,
            districtId = Guid.NewGuid(),
            city = "Atlantis",
            country = "Neverland"
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var created = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var listingId = created.RootElement.GetProperty("id").GetGuid();

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await db.Listings.SingleAsync(l => l.Id == listingId);

        Assert.Equal(TestData.KentronPoint.Latitude, stored.Latitude);
        Assert.Equal(TestData.KentronPoint.Longitude, stored.Longitude);
        Assert.Equal("Yerevan", stored.City);
        Assert.Equal("Armenia", stored.Country);
        Assert.NotEqual(12.3456m, stored.Latitude);
    }

    // The UPDATE half of the same property, and the one that was actually broken: `country` stayed
    // on UpdateListingRequest behind nothing but a length check long after `city` and `districtId`
    // came off it, so `{"country":"Neverland"}` was accepted and STUCK — a listing update is not
    // re-moderated, HomePointService only re-asserts Country when the home point moves, and
    // ListingLocationBackfillRunner reconciles neither City nor Country. The row then published a
    // Yerevan district and a Yerevan pin next to a country that does not exist.
    [Fact]
    public async Task Patch_Listing_Ignores_Location_Fields_A_Stale_Client_Still_Sends()
    {
        var ownerId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var listingId = Guid.NewGuid();
        var email = $"{ownerId:N}@listing-owner.local";

        await _factory.SeedAsync(
            TestData.OwnerWithHome(ownerId, email, TestData.KentronPoint),
            TestData.Category(categoryId));

        var listing = TestData.Listing(listingId, ownerId, categoryId, ListingStatus.Approved);
        listing.Latitude = TestData.KentronPoint.Latitude;
        listing.Longitude = TestData.KentronPoint.Longitude;
        await _factory.SeedAsync(listing);

        // Raw wire shape, with one field that IS writable alongside the location ones, so a 204
        // cannot be mistaken for "the whole body was rejected".
        var response = await ClientFor(ownerId, email).PatchAsJsonAsync($"/api/listings/{listingId}", new
        {
            pricePerDay = 3100,
            latitude = 12.3456,
            longitude = 65.4321,
            districtId = Guid.NewGuid(),
            city = "Atlantis",
            country = "Neverland"
        });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await db.Listings.SingleAsync(l => l.Id == listingId);

        Assert.Equal(3100m, stored.PricePerDay);          // the writable field did apply
        Assert.Equal("Armenia", stored.Country);
        Assert.Equal("Yerevan", stored.City);
        Assert.Equal(TestData.KentronPoint.Latitude, stored.Latitude);
        Assert.Equal(TestData.KentronPoint.Longitude, stored.Longitude);
    }

    // ADR-040: unit tests with C# object initialisers cannot prove the wire behaviour. This sends RAW
    // JSON so the real model binder + Optional<T> converter decide: absent leaves, null clears.
    private async Task<(HttpClient Client, Guid ListingId)> SeedListingWithOptionalsAsync()
    {
        var ownerId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var listingId = Guid.NewGuid();
        var email = $"{ownerId:N}@listing-owner.local";

        await _factory.SeedAsync(
            TestData.OwnerWithHome(ownerId, email, TestData.KentronPoint),
            TestData.Category(categoryId));

        var listing = TestData.Listing(listingId, ownerId, categoryId, ListingStatus.Approved);
        listing.AgeFromMonths = 12;
        listing.AgeToMonths = 60;
        listing.Condition = "Good";
        listing.HygieneNotes = "Washed";
        listing.SafetyNotes = "No small parts";
        listing.MinRentalDays = 3;
        listing.CompensationAmount = 5000m;
        await _factory.SeedAsync(listing);

        return (ClientFor(ownerId, email), listingId);
    }

    private static Task<HttpResponseMessage> PatchRawAsync(HttpClient client, Guid listingId, string json) =>
        client.SendAsync(new HttpRequestMessage(HttpMethod.Patch, $"/api/listings/{listingId}")
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        });

    private async Task<RentalPlatform.Domain.Entities.Listing> LoadListingAsync(Guid listingId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Listings.AsNoTracking().SingleAsync(l => l.Id == listingId);
    }

    [Fact]
    public async Task Patch_Listing_Absent_Optionals_Leave_Values_And_Explicit_Null_Clears_Them()
    {
        var (client, listingId) = await SeedListingWithOptionalsAsync();

        // Absent: only the price is on the wire, so all seven optionals must survive.
        var absent = await PatchRawAsync(client, listingId, """{"pricePerDay":3100}""");
        Assert.Equal(HttpStatusCode.NoContent, absent.StatusCode);

        var afterAbsent = await LoadListingAsync(listingId);
        Assert.Equal(3100m, afterAbsent.PricePerDay);
        Assert.Equal(12, afterAbsent.AgeFromMonths);
        Assert.Equal(60, afterAbsent.AgeToMonths);
        Assert.Equal("Good", afterAbsent.Condition);
        Assert.Equal("Washed", afterAbsent.HygieneNotes);
        Assert.Equal("No small parts", afterAbsent.SafetyNotes);
        Assert.Equal(3, afterAbsent.MinRentalDays);
        Assert.Equal(5000m, afterAbsent.CompensationAmount);
        Assert.Equal(ListingStatus.Approved, afterAbsent.Status);

        // Explicit null: the same six keys, now present as null, must clear.
        var cleared = await PatchRawAsync(client, listingId,
            """{"ageFromMonths":null,"ageToMonths":null,"condition":null,"hygieneNotes":null,"safetyNotes":null,"minRentalDays":null}""");
        Assert.Equal(HttpStatusCode.NoContent, cleared.StatusCode);

        var afterNull = await LoadListingAsync(listingId);
        Assert.Null(afterNull.AgeFromMonths);
        Assert.Null(afterNull.AgeToMonths);
        Assert.Null(afterNull.Condition);
        Assert.Null(afterNull.HygieneNotes);
        Assert.Null(afterNull.SafetyNotes);
        Assert.Null(afterNull.MinRentalDays);
        Assert.Equal(5000m, afterNull.CompensationAmount);
        Assert.Equal(ListingStatus.PendingApproval, afterNull.Status); // text cleared on an approved listing
    }

    [Fact]
    public async Task Patch_Listing_Explicit_Null_CompensationAmount_Is_400_And_Absent_Is_Not()
    {
        var (client, listingId) = await SeedListingWithOptionalsAsync();

        var rejected = await PatchRawAsync(client, listingId, """{"compensationAmount":null}""");
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Contains("Loss & damage compensation cannot be cleared.", await rejected.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(5000m, (await LoadListingAsync(listingId)).CompensationAmount);

        var setValue = await PatchRawAsync(client, listingId, """{"compensationAmount":7000}""");
        Assert.Equal(HttpStatusCode.NoContent, setValue.StatusCode);
        Assert.Equal(7000m, (await LoadListingAsync(listingId)).CompensationAmount);
    }

    [Fact]
    public async Task Patch_Listing_Out_Of_Range_Optional_Is_400_With_The_Original_Message()
    {
        var (client, listingId) = await SeedListingWithOptionalsAsync();

        var response = await PatchRawAsync(client, listingId, """{"minRentalDays":0}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Minimum rental days must be between 1 and 365.", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Patch_Listing_Malformed_Optional_Reports_The_Property_Path()
    {
        var (client, listingId) = await SeedListingWithOptionalsAsync();

        var response = await PatchRawAsync(client, listingId, """{"ageFromMonths":"abc"}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var doc = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var keys = doc.RootElement.GetProperty("errors").EnumerateObject().Select(p => p.Name).ToList();
        Assert.Contains("$.ageFromMonths", keys);
    }

    // M-038: the home-point requirement is new, and owners who published before it existed still
    // have listings. Tightening CREATE must not make their existing listings uneditable — a dead
    // Save button with no explanation is exactly the trap that mistake records.
    [Fact]
    public async Task A_Legacy_Owner_With_No_Home_Point_Can_Still_Edit_Archive_And_Restore()
    {
        var ownerId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var listingId = Guid.NewGuid();
        var email = $"{ownerId:N}@listing-owner.local";

        // No home point anywhere — the shape a pre-feature row actually has.
        await _factory.SeedAsync(TestData.User(ownerId, email), TestData.Category(categoryId));
        var listing = TestData.Listing(listingId, ownerId, categoryId, ListingStatus.Approved);
        listing.Latitude = 40.1776m;
        listing.Longitude = 44.5126m;
        await _factory.SeedAsync(listing);

        var client = ClientFor(ownerId, email);

        var patch = await client.PatchAsJsonAsync($"/api/listings/{listingId}", new { pricePerDay = 3100 });
        Assert.Equal(HttpStatusCode.NoContent, patch.StatusCode);

        var archive = await client.PostAsync($"/api/listings/{listingId}/archive", null);
        Assert.Equal(HttpStatusCode.NoContent, archive.StatusCode);

        var restore = await client.PostAsync($"/api/listings/{listingId}/restore", null);
        Assert.Equal(HttpStatusCode.NoContent, restore.StatusCode);

        // And the listing kept the location it already had — the edit path must never wipe a legacy
        // row's coordinates just because its owner has no home point to copy.
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await db.Listings.SingleAsync(l => l.Id == listingId);
        Assert.Equal(40.1776m, stored.Latitude);
    }

    // M-038 again, for the Yerevan-only rule specifically. The AddUserHomePoint migration derived
    // home points from existing listings WITHOUT applying that rule, so a user migrated from a
    // listing outside Yerevan now holds a home point that a new save would refuse. They are not
    // re-validated: the rule governs new writes, not rows that predate it. If it were applied
    // retroactively, an owner would find their whole catalogue frozen — unable to edit, unable to
    // archive — by a rule that did not exist when they published.
    [Fact]
    public async Task A_Legacy_Owner_Whose_Home_Point_Is_Outside_Yerevan_Can_Still_Edit_Archive_And_Restore()
    {
        var ownerId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var listingId = Guid.NewGuid();
        var email = $"{ownerId:N}@listing-owner.local";
        var (legacyLatitude, legacyLongitude) = TestData.OutsideYerevanPoint;

        // Constructed directly in the database, exactly as the migration would have left it: an
        // exact home point outside Yerevan, no district. This shape cannot be produced through the
        // API any more, which is precisely why it is built by hand here.
        var legacyOwner = TestData.User(
            ownerId, email,
            homeLatitude: legacyLatitude,
            homeLongitude: legacyLongitude,
            homeDistrictId: null);
        await _factory.SeedAsync(legacyOwner, TestData.Category(categoryId));

        var listing = TestData.Listing(listingId, ownerId, categoryId, ListingStatus.Approved);
        listing.Latitude = legacyLatitude;
        listing.Longitude = legacyLongitude;
        listing.City = "Gyumri";
        await _factory.SeedAsync(listing);

        var client = ClientFor(ownerId, email);

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await client.PatchAsJsonAsync($"/api/listings/{listingId}", new { pricePerDay = 3100 })).StatusCode);
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await client.PostAsync($"/api/listings/{listingId}/archive", null)).StatusCode);
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await client.PostAsync($"/api/listings/{listingId}/restore", null)).StatusCode);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await db.Listings.SingleAsync(l => l.Id == listingId);

        // Its out-of-area location survives untouched — editing is not a relocation, and nothing
        // relabels a legacy row's city to "Yerevan" behind the owner's back.
        Assert.Equal(legacyLatitude, stored.Latitude);
        Assert.Equal("Gyumri", stored.City);

        // But a NEW home-point write from that same account is still refused: the account is not
        // grandfathered, only the row it already holds is.
        var move = await client.PutAsJsonAsync(
            "/api/auth/me/home-point",
            new { latitude = legacyLatitude, longitude = legacyLongitude });

        Assert.Equal(HttpStatusCode.BadRequest, move.StatusCode);
        Assert.Contains("auth.home_point_outside_yerevan", await move.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Patch_With_LegacyOnly_DeliveryType_Json_Body_Preserves_Multi_Select_DeliveryOptions()
    {
        var ownerId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var listingId = Guid.NewGuid();
        var email = $"{ownerId:N}@listing-owner.local";

        await _factory.SeedAsync(
            TestData.OwnerWithHome(ownerId, email),
            TestData.Category(categoryId));
        var listing = TestData.Listing(listingId, ownerId, categoryId, ListingStatus.Approved);
        listing.DeliveryOptions = DeliveryOptions.Pickup | DeliveryOptions.Courier;
        listing.DeliveryType = DeliveryType.Pickup;
        await _factory.SeedAsync(listing);

        var client = ClientFor(ownerId, email);

        // Raw wire shape of the stale pre-deploy edit form: only the legacy scalar field,
        // "deliveryTypes" key entirely absent from the JSON body — not merely null in a C# object.
        var patchResponse = await client.PatchAsJsonAsync(
            $"/api/listings/{listingId}",
            new { deliveryType = "Pickup" });

        Assert.Equal(HttpStatusCode.NoContent, patchResponse.StatusCode);

        var mineResponse = await client.GetAsync("/api/listings/mine");
        Assert.Equal(HttpStatusCode.OK, mineResponse.StatusCode);

        using var doc = JsonDocument.Parse(await mineResponse.Content.ReadAsStringAsync());
        var updated = doc.RootElement.EnumerateArray()
            .Single(e => e.GetProperty("id").GetGuid() == listingId);

        var deliveryTypes = updated.GetProperty("deliveryTypes").EnumerateArray()
            .Select(e => e.GetString())
            .ToArray();
        Assert.Equal(new[] { "Pickup", "Courier" }, deliveryTypes);
        Assert.Equal("Pickup", updated.GetProperty("deliveryType").GetString());
    }
}
