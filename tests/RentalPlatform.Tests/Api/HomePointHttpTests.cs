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

// The home-point HTTP surface end to end: PUT /api/auth/me/home-point, DELETE of the same, and the
// anonymous district lookup the sign-up map calls while the pin is being dragged.
//
// These go through the real pipeline — model binding, DataAnnotations, [Authorize], the service and
// the error-code-to-status mapping — because that mapping is the contract the client codes against,
// and none of it is exercised by a service-level test.
[Collection("Integration")]
public sealed class HomePointHttpTests
{
    private readonly RentalPlatformWebAppFactory _factory;

    public HomePointHttpTests(RentalPlatformWebAppFactory factory) => _factory = factory;

    private HttpClient ClientFor(Guid userId, string email)
    {
        var token = TestJwtTokenHelper.GenerateToken(userId, email);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    // ---- PUT /api/auth/me/home-point ----------------------------------------------------------

    [Fact]
    public async Task Put_HomePoint_Returns_200_With_The_Point_On_CurrentUser()
    {
        var userId = Guid.NewGuid();
        var email = $"{userId:N}@home-point.local";
        await _factory.SeedAsync(TestData.User(userId, email));

        var client = ClientFor(userId, email);
        var (latitude, longitude) = TestData.KentronPoint;

        var response = await client.PutAsJsonAsync(
            "/api/auth/me/home-point",
            new { latitude, longitude });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var homePoint = doc.RootElement.GetProperty("homePoint");

        Assert.Equal(latitude, homePoint.GetProperty("latitude").GetDecimal());
        Assert.Equal(longitude, homePoint.GetProperty("longitude").GetDecimal());
        // The published pair is the geohash cell centroid, never the exact point (ADR-008).
        Assert.NotEqual(latitude, homePoint.GetProperty("publicLatitude").GetDecimal());
        Assert.Equal("kentron", homePoint.GetProperty("district").GetProperty("code").GetString());
        Assert.NotEqual(JsonValueKind.Null, homePoint.GetProperty("updatedAt").ValueKind);
    }

    // GET /me must report the same thing the PUT returned — the client refreshes through it.
    [Fact]
    public async Task Get_Me_Reports_The_Home_Point_After_It_Is_Set()
    {
        var userId = Guid.NewGuid();
        var email = $"{userId:N}@home-point.local";
        await _factory.SeedAsync(TestData.User(userId, email));

        var client = ClientFor(userId, email);
        await client.PutAsJsonAsync(
            "/api/auth/me/home-point",
            new { latitude = TestData.KentronPoint.Latitude, longitude = TestData.KentronPoint.Longitude });

        var response = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            TestData.KentronPoint.Latitude,
            doc.RootElement.GetProperty("homePoint").GetProperty("latitude").GetDecimal());
    }

    // A user who has never set one gets an explicit null, not a missing key — the sign-up wizard
    // branches on it.
    [Fact]
    public async Task Get_Me_Reports_A_Null_Home_Point_When_None_Is_Set()
    {
        var userId = Guid.NewGuid();
        var email = $"{userId:N}@home-point.local";
        await _factory.SeedAsync(TestData.User(userId, email));

        var response = await ClientFor(userId, email).GetAsync("/api/auth/me");

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("homePoint").ValueKind);
    }

    [Theory]
    // Out of WGS84 range entirely — rejected by the DTO's [Range].
    [InlineData(95.0, 44.5)]
    [InlineData(40.18, 200.0)]
    // Well-formed coordinates that are simply not in Yerevan — rejected by the service.
    [InlineData(51.5074, -0.1278)]   // London
    [InlineData(40.7894, 43.8475)]   // Gyumri
    public async Task Put_HomePoint_Returns_400_For_A_Point_We_Do_Not_Accept(double latitude, double longitude)
    {
        var userId = Guid.NewGuid();
        var email = $"{userId:N}@home-point.local";
        await _factory.SeedAsync(TestData.User(userId, email));

        var response = await ClientFor(userId, email).PutAsJsonAsync(
            "/api/auth/me/home-point",
            new { latitude, longitude });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // The out-of-area rejection carries a machine-readable token, because the client renders it as
    // a blocking inline error and a popup, both translated. One stable code for every out-of-area
    // point — "in Armenia but not Yerevan" is not a softer case, it is the same refusal.
    [Theory]
    [InlineData(40.7894, 43.8475)]   // Gyumri
    [InlineData(51.5074, -0.1278)]   // London
    public async Task Put_HomePoint_Outside_Yerevan_Names_The_Error_Code_In_The_Response(double latitude, double longitude)
    {
        var userId = Guid.NewGuid();
        var email = $"{userId:N}@home-point.local";
        await _factory.SeedAsync(TestData.User(userId, email));

        var response = await ClientFor(userId, email).PutAsJsonAsync(
            "/api/auth/me/home-point",
            new { latitude, longitude });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("auth.home_point_outside_yerevan", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    // The refusal applies to everyone, not only to owners. A renter has a home point too (it is what
    // "distance from home" measures from), and Yerevan-only is a platform rule, not a listing rule.
    [Fact]
    public async Task Put_HomePoint_Outside_Yerevan_Is_Refused_For_A_User_Who_Owns_Nothing()
    {
        var userId = Guid.NewGuid();
        var email = $"{userId:N}@home-point.local";
        await _factory.SeedAsync(TestData.User(userId, email));

        var client = ClientFor(userId, email);
        var (latitude, longitude) = TestData.OutsideYerevanPoint;

        var response = await client.PutAsJsonAsync("/api/auth/me/home-point", new { latitude, longitude });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var me = await client.GetAsync("/api/auth/me");
        using var doc = JsonDocument.Parse(await me.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("homePoint").ValueKind);
    }

    [Fact]
    public async Task Put_HomePoint_Returns_400_When_Only_One_Coordinate_Is_Supplied()
    {
        var userId = Guid.NewGuid();
        var email = $"{userId:N}@home-point.local";
        await _factory.SeedAsync(TestData.User(userId, email));

        var response = await ClientFor(userId, email).PutAsJsonAsync(
            "/api/auth/me/home-point",
            new { latitude = TestData.KentronPoint.Latitude });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Put_HomePoint_Returns_401_Without_A_Token()
    {
        var response = await _factory.CreateClient().PutAsJsonAsync(
            "/api/auth/me/home-point",
            new { latitude = TestData.KentronPoint.Latitude, longitude = TestData.KentronPoint.Longitude });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Put_HomePoint_Returns_403_For_A_Blocked_User()
    {
        var userId = Guid.NewGuid();
        var email = $"{userId:N}@home-point.local";
        await _factory.SeedAsync(TestData.User(userId, email, isBlocked: true));

        var response = await ClientFor(userId, email).PutAsJsonAsync(
            "/api/auth/me/home-point",
            new { latitude = TestData.KentronPoint.Latitude, longitude = TestData.KentronPoint.Longitude });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // Moving the point moves every listing the owner has — the whole promise of the feature, here
    // proved through the public API rather than the service.
    [Fact]
    public async Task Put_HomePoint_Moves_The_Owners_Listings()
    {
        var ownerId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var listingId = Guid.NewGuid();
        var email = $"{ownerId:N}@home-point.local";

        await _factory.SeedAsync(
            TestData.OwnerWithHome(ownerId, email, TestData.KentronPoint),
            TestData.Category(categoryId));
        await _factory.SeedAsync(TestData.Listing(listingId, ownerId, categoryId, ListingStatus.Approved));

        var client = ClientFor(ownerId, email);
        var (latitude, longitude) = TestData.ArabkirPoint;

        var response = await client.PutAsJsonAsync("/api/auth/me/home-point", new { latitude, longitude });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var mine = await client.GetAsync("/api/listings/mine");
        Assert.Equal(HttpStatusCode.OK, mine.StatusCode);

        // The owner-facing list does not publish coordinates, so the move is verified against the
        // stored row.
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await db.Listings.SingleAsync(l => l.Id == listingId);

        Assert.Equal(latitude, stored.Latitude);
        Assert.Equal(TestData.ArabkirDistrictId, stored.DistrictId);
        // City moves with the pin, so it can never end up naming the city the owner left.
        Assert.Equal("Yerevan", stored.City);
        Assert.Equal("Armenia", stored.Country);
    }

    // ---- DELETE /api/auth/me/home-point --------------------------------------------------------

    [Fact]
    public async Task Delete_HomePoint_Returns_200_And_Clears_It_For_A_User_Who_Owns_Nothing()
    {
        var userId = Guid.NewGuid();
        var email = $"{userId:N}@home-point.local";
        await _factory.SeedAsync(TestData.OwnerWithHome(userId, email));

        var response = await ClientFor(userId, email).DeleteAsync("/api/auth/me/home-point");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("homePoint").ValueKind);
    }

    // 409 rather than 403: the caller is entitled to do this, just not while they still have
    // listings that would be left with nowhere to be.
    [Fact]
    public async Task Delete_HomePoint_Returns_409_While_The_User_Owns_A_Listing()
    {
        var ownerId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var email = $"{ownerId:N}@home-point.local";

        await _factory.SeedAsync(
            TestData.OwnerWithHome(ownerId, email),
            TestData.Category(categoryId));
        await _factory.SeedAsync(TestData.Listing(Guid.NewGuid(), ownerId, categoryId, ListingStatus.Archived));

        var response = await ClientFor(ownerId, email).DeleteAsync("/api/auth/me/home-point");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("auth.home_point_in_use", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Delete_HomePoint_Returns_401_Without_A_Token()
    {
        var response = await _factory.CreateClient().DeleteAsync("/api/auth/me/home-point");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Delete_HomePoint_Returns_403_For_A_Blocked_User()
    {
        var userId = Guid.NewGuid();
        var email = $"{userId:N}@home-point.local";
        await _factory.SeedAsync(TestData.OwnerWithHome(userId, email, isBlocked: true));

        var response = await ClientFor(userId, email).DeleteAsync("/api/auth/me/home-point");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
