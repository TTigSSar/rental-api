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

// ADR-030 sections 5 and 8: PUT /api/auth/me/name, PUT /api/auth/me/phone and the phone gate on
// POST /api/listings and POST /api/bookings. Both per-user rate limits are partitioned by account,
// so every test uses a fresh user and needs no IP isolation.
[Collection("Integration")]
public sealed class NameAndPhoneHttpTests
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    private readonly RentalPlatformWebAppFactory _factory;

    public NameAndPhoneHttpTests(RentalPlatformWebAppFactory factory) => _factory = factory;

    private sealed record Account(Guid Id, string Email, HttpClient Client);

    private async Task<Account> NewAccountAsync(string? phone = TestData.DefaultPhone, bool withHome = false, bool blocked = false)
    {
        var id = Guid.NewGuid();
        var email = $"{id:N}@name-phone.local";
        await _factory.SeedAsync(withHome
            ? TestData.OwnerWithHome(id, email, isBlocked: blocked, phoneNumber: phone)
            : TestData.User(id, email, isBlocked: blocked, phoneNumber: phone));
        return new Account(id, email, ClientFor(id, email));
    }

    private static int _ipCounter;

    private HttpClient ClientFor(Guid id, string email)
    {
        var client = _factory.CreateClient();
        // Own simulated IP per client: booking creation sits behind a per-IP bucket that other
        // classes in the collection (BookingHttpTests) share.
        var n = Interlocked.Increment(ref _ipCounter);
        client.DefaultRequestHeaders.Add(TestRemoteIpStartupFilter.HeaderName, $"10.55.{(n >> 8) & 0xFF}.{n & 0xFF}");
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestJwtTokenHelper.GenerateToken(id, email));
        return client;
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private static async Task<string> ErrorCodeAsync(HttpResponseMessage response) =>
        (await JsonAsync(response)).GetProperty("errorCode").GetString()!;

    private async Task<JsonElement> MeAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await JsonAsync(response);
    }

    // ---- Name ----

    [Fact]
    public async Task Name_Put_Returns_204_Trims_And_Is_Visible_On_Me()
    {
        var a = await NewAccountAsync();

        var response = await a.Client.PutAsJsonAsync("/api/auth/me/name", new { firstName = "  Anna ", lastName = " Petrosyan  " });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var me = await MeAsync(a.Client);
        Assert.Equal("Anna", me.GetProperty("firstName").GetString());
        Assert.Equal("Petrosyan", me.GetProperty("lastName").GetString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Name_Put_Without_A_Last_Name_Stores_An_Empty_One(string? lastName)
    {
        var a = await NewAccountAsync();

        var response = await a.Client.PutAsJsonAsync("/api/auth/me/name", new { firstName = "Anna", lastName });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(string.Empty, (await MeAsync(a.Client)).GetProperty("lastName").GetString());
    }

    [Fact]
    public async Task Name_Put_Without_The_LastName_Property_Is_Accepted()
    {
        var a = await NewAccountAsync();

        var response = await a.Client.PutAsJsonAsync("/api/auth/me/name", new { firstName = "Anna" });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public async Task Name_Put_With_A_Blank_First_Name_Is_400_And_Changes_Nothing(string firstName)
    {
        var a = await NewAccountAsync();

        var response = await a.Client.PutAsJsonAsync("/api/auth/me/name", new { firstName, lastName = "X" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Test", (await MeAsync(a.Client)).GetProperty("firstName").GetString());
    }

    [Fact]
    public async Task Name_Put_Without_A_First_Name_Is_400()
    {
        var a = await NewAccountAsync();

        var response = await a.Client.PutAsJsonAsync("/api/auth/me/name", new { lastName = "Petrosyan" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Name_Limits_Apply_After_Trimming()
    {
        var a = await NewAccountAsync();

        // 100 characters plus padding is fine; 101 real characters is not - for either name.
        var ok = await a.Client.PutAsJsonAsync("/api/auth/me/name", new { firstName = "  " + new string('a', 100) + "  ", lastName = new string('b', 100) });
        var tooLongFirst = await a.Client.PutAsJsonAsync("/api/auth/me/name", new { firstName = new string('a', 101) });
        var tooLongLast = await a.Client.PutAsJsonAsync("/api/auth/me/name", new { firstName = "Anna", lastName = new string('b', 101) });

        Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, tooLongFirst.StatusCode);
        Assert.Equal("auth.invalid_name", await ErrorCodeAsync(tooLongFirst));
        Assert.Equal(HttpStatusCode.BadRequest, tooLongLast.StatusCode);
        Assert.Equal("auth.invalid_name", await ErrorCodeAsync(tooLongLast));
    }

    [Fact]
    public async Task Name_Put_Anonymous_Is_401()
    {
        var response = await _factory.CreateClient().PutAsJsonAsync("/api/auth/me/name", new { firstName = "Anna" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Name_Put_Returns_429_After_Ten_Per_Hour_Per_User_And_Another_User_Is_Unaffected()
    {
        var a = await NewAccountAsync();
        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(HttpStatusCode.NoContent, (await a.Client.PutAsJsonAsync("/api/auth/me/name", new { firstName = $"Anna{i}" })).StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await a.Client.PutAsJsonAsync("/api/auth/me/name", new { firstName = "Anna" })).StatusCode);

        var b = await NewAccountAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await b.Client.PutAsJsonAsync("/api/auth/me/name", new { firstName = "Anna" })).StatusCode);
    }

    // ---- Phone ----

    [Fact]
    public async Task Phone_Put_Returns_204_Trims_And_Is_Visible_On_Me()
    {
        var a = await NewAccountAsync(phone: null);
        Assert.Equal(JsonValueKind.Null, (await MeAsync(a.Client)).GetProperty("phoneNumber").ValueKind);

        var response = await a.Client.PutAsJsonAsync("/api/auth/me/phone", new { phoneNumber = "+374 99 123456  " });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("+374 99 123456", (await MeAsync(a.Client)).GetProperty("phoneNumber").GetString());
    }

    [Fact]
    public async Task Phone_Put_Replaces_An_Existing_Phone()
    {
        var a = await NewAccountAsync(phone: "+374 99 000000");

        var response = await a.Client.PutAsJsonAsync("/api/auth/me/phone", new { phoneNumber = "+374 77 654321" });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("+374 77 654321", (await MeAsync(a.Client)).GetProperty("phoneNumber").GetString());
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("12345")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("+374 99 123456 ext 7")]
    public async Task Phone_Put_With_An_Invalid_Number_Is_400_And_Keeps_The_Old_One(string phone)
    {
        var a = await NewAccountAsync(phone: "+374 99 000000");

        var response = await a.Client.PutAsJsonAsync("/api/auth/me/phone", new { phoneNumber = phone });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("+374 99 000000", (await MeAsync(a.Client)).GetProperty("phoneNumber").GetString());
    }

    [Fact]
    public async Task Phone_Put_Longer_Than_32_Characters_Is_400()
    {
        var a = await NewAccountAsync();

        var response = await a.Client.PutAsJsonAsync("/api/auth/me/phone", new { phoneNumber = "+" + new string('1', 33) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Phone_Put_Anonymous_Is_401()
    {
        var response = await _factory.CreateClient().PutAsJsonAsync("/api/auth/me/phone", new { phoneNumber = "+374 99 123456" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Phone_Put_Returns_429_After_Ten_Per_Hour_Per_User_And_Name_Has_Its_Own_Budget()
    {
        var a = await NewAccountAsync();
        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(HttpStatusCode.NoContent, (await a.Client.PutAsJsonAsync("/api/auth/me/phone", new { phoneNumber = $"+374 99 12345{i}" })).StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await a.Client.PutAsJsonAsync("/api/auth/me/phone", new { phoneNumber = "+374 99 123456" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await a.Client.PutAsJsonAsync("/api/auth/me/name", new { firstName = "Anna" })).StatusCode);
    }

    // ---- Phone gate: listings ----

    private static object ListingBody(Guid categoryId) => new
    {
        categoryId,
        title = "Wooden Train Set",
        description = "A long enough description to satisfy validation rules.",
        pricePerDay = 2500,
        compensationAmount = 12000
    };

    private async Task<int> ListingCountAsync(Guid ownerId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Listings.CountAsync(listing => listing.OwnerId == ownerId);
    }

    private async Task<int> BookingCountAsync(Guid renterId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Bookings.CountAsync(booking => booking.RenterId == renterId);
    }

    [Fact]
    public async Task Listing_Create_Without_A_Phone_Is_409_phone_required_Writes_Nothing_And_Retries_After_Adding_One()
    {
        var a = await NewAccountAsync(phone: null, withHome: true);
        var categoryId = Guid.NewGuid();
        await _factory.SeedAsync(TestData.Category(categoryId));

        var refused = await a.Client.PostAsJsonAsync("/api/listings", ListingBody(categoryId));

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("listing.phone_required", await ErrorCodeAsync(refused));
        Assert.Equal(0, await ListingCountAsync(a.Id));

        Assert.Equal(HttpStatusCode.NoContent, (await a.Client.PutAsJsonAsync("/api/auth/me/phone", new { phoneNumber = "+374 99 123456" })).StatusCode);
        var created = await a.Client.PostAsJsonAsync("/api/listings", ListingBody(categoryId));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(1, await ListingCountAsync(a.Id));
    }

    [Fact]
    public async Task Listing_Phone_Gate_Runs_Before_The_Category_And_Home_Point_Checks()
    {
        // No home point and an unknown category: the phone answer still comes first.
        var a = await NewAccountAsync(phone: null, withHome: false);

        var response = await a.Client.PostAsJsonAsync("/api/listings", ListingBody(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("listing.phone_required", await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task Listing_Create_With_A_Blank_Phone_Is_Also_Refused()
    {
        var a = await NewAccountAsync(phone: "   ", withHome: true);
        var categoryId = Guid.NewGuid();
        await _factory.SeedAsync(TestData.Category(categoryId));

        var response = await a.Client.PostAsJsonAsync("/api/listings", ListingBody(categoryId));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("listing.phone_required", await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task Blocked_Without_A_Phone_Is_403_Not_409_And_Anonymous_Is_401()
    {
        var blocked = await NewAccountAsync(phone: null, withHome: true, blocked: true);
        var categoryId = Guid.NewGuid();
        await _factory.SeedAsync(TestData.Category(categoryId));

        var forbidden = await blocked.Client.PostAsJsonAsync("/api/listings", ListingBody(categoryId));
        var anonymous = await _factory.CreateClient().PostAsJsonAsync("/api/listings", ListingBody(categoryId));

        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal("listing.user_blocked", await ErrorCodeAsync(forbidden));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    // ---- Phone gate: bookings ----

    private async Task<Guid> ApprovedListingAsync()
    {
        var ownerId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var listingId = Guid.NewGuid();
        await _factory.SeedAsync(TestData.User(ownerId, $"{ownerId:N}@gate-owner.local"), TestData.Category(categoryId));
        await _factory.SeedAsync(TestData.Listing(listingId, ownerId, categoryId, ListingStatus.Approved));
        return listingId;
    }

    private static object BookingBody(Guid listingId) => new
    {
        listingId,
        startDate = Today.AddDays(40).ToString("yyyy-MM-dd"),
        endDate = Today.AddDays(42).ToString("yyyy-MM-dd")
    };

    [Fact]
    public async Task Booking_Create_Without_A_Phone_Is_409_phone_required_Writes_Nothing_And_Retries_After_Adding_One()
    {
        var renter = await NewAccountAsync(phone: null);
        var listingId = await ApprovedListingAsync();

        var refused = await renter.Client.PostAsJsonAsync("/api/bookings", BookingBody(listingId));

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("booking.phone_required", await ErrorCodeAsync(refused));
        Assert.Equal(0, await BookingCountAsync(renter.Id));

        Assert.Equal(HttpStatusCode.NoContent, (await renter.Client.PutAsJsonAsync("/api/auth/me/phone", new { phoneNumber = "+374 99 123456" })).StatusCode);
        var created = await renter.Client.PostAsJsonAsync("/api/bookings", BookingBody(listingId));

        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        Assert.Equal(1, await BookingCountAsync(renter.Id));
    }

    [Fact]
    public async Task Booking_Phone_Gate_Runs_Before_The_Listing_Lookup_And_Date_Checks()
    {
        var renter = await NewAccountAsync(phone: null);

        var response = await renter.Client.PostAsJsonAsync("/api/bookings", new
        {
            listingId = Guid.NewGuid(),
            startDate = Today.AddDays(-5).ToString("yyyy-MM-dd"),
            endDate = Today.AddDays(-9).ToString("yyyy-MM-dd")
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("booking.phone_required", await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task Booking_Create_By_A_Blocked_User_Without_A_Phone_Is_403_Not_409()
    {
        var renter = await NewAccountAsync(phone: null, blocked: true);
        var listingId = await ApprovedListingAsync();

        var response = await renter.Client.PostAsJsonAsync("/api/bookings", BookingBody(listingId));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("booking.user_blocked", await ErrorCodeAsync(response));
    }
}
