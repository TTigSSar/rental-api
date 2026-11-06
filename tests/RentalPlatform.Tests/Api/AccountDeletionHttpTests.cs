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

// ADR-056 through the REAL pipeline: routing, [Authorize] with the real JWT validation (ADR-055), the
// rate limiter, the ProblemDetails wire shape, the real stores. Each test pins its own simulated client IP
// because the "auth" bucket (5 per minute) is per IP and the whole "Integration" collection shares one host.
// The race on a lost compare-and-set (409 account.state_changed) cannot be driven through HTTP without a
// second writer inside the transaction; it is pinned at the service level (AccountDeletionServiceTests).
[Collection("Integration")]
public sealed class AccountDeletionHttpTests
{
    private const string Password = "Secret1234";

    private static int _ipCounter = 10;

    private readonly RentalPlatformWebAppFactory _factory;

    public AccountDeletionHttpTests(RentalPlatformWebAppFactory factory) => _factory = factory;

    private static string NextIp() => $"10.77.0.{Interlocked.Increment(ref _ipCounter)}";

    private HttpClient Client(string? token = null)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestRemoteIpStartupFilter.HeaderName, NextIp());
        if (token is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return client;
    }

    private async Task<(Guid Id, string Email, string Token)> SeedUserAsync(
        UserRole role = UserRole.User, bool blocked = false, string? passwordHash = "bcrypt")
    {
        var id = Guid.NewGuid();
        var email = $"{id:N}@deletion.local";
        var hash = passwordHash == "bcrypt" ? BCrypt.Net.BCrypt.HashPassword(Password) : passwordHash;
        await _factory.SeedAsync(TestData.User(id, email, isBlocked: blocked, role: role, passwordHash: hash));
        return (id, email, TestJwtTokenHelper.GenerateToken(id, email, role));
    }

    private static Task<HttpResponseMessage> DeleteAsync(HttpClient client, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, "/api/auth/me") { Content = JsonContent.Create(body) };
        return client.SendAsync(request);
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private async Task<(Guid ListingId, Guid CategoryId)> SeedOwnerListingAsync(Guid ownerId)
    {
        var categoryId = Guid.NewGuid();
        var listingId = Guid.NewGuid();
        await _factory.SeedAsync(TestData.Category(categoryId));
        await _factory.SeedAsync(TestData.Listing(listingId, ownerId, categoryId));
        return (listingId, categoryId);
    }

    [Fact]
    public async Task Delete_Returns_204_Then_The_Old_Token_Is_401_And_A_Retry_Is_401()
    {
        var (_, _, token) = await SeedUserAsync();
        var client = Client(token);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);

        var response = await DeleteAsync(client, new { password = Password });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await DeleteAsync(client, new { password = Password })).StatusCode);
    }

    [Fact]
    public async Task After_Deletion_Login_With_The_Old_Credentials_Fails()
    {
        var (id, email, token) = await SeedUserAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await DeleteAsync(Client(token), new { password = Password })).StatusCode);

        var login = await Client().PostAsJsonAsync("/api/auth/login", new { email, password = Password });

        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
        Assert.Equal("auth.invalid_credentials", (await ReadAsync(login)).GetProperty("errorCode").GetString());

        await using var scope = _factory.Services.CreateAsyncScope();
        var stored = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.AsNoTracking().SingleAsync(user => user.Id == id);
        Assert.NotNull(stored.DeletedAt);
        Assert.Equal($"deleted-{id}@deleted.invalid", stored.Email);
    }

    [Fact]
    public async Task A_Wrong_Password_Is_400_With_The_Confirmation_Error_Code_And_The_Account_Survives()
    {
        var (_, _, token) = await SeedUserAsync();
        var client = Client(token);

        var response = await DeleteAsync(client, new { password = "nope-nope" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("account.invalid_confirmation", (await ReadAsync(response)).GetProperty("errorCode").GetString());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task An_Admin_Gets_403_With_The_Admin_Error_Code()
    {
        var (_, _, token) = await SeedUserAsync(UserRole.Admin);

        var response = await DeleteAsync(Client(token), new { password = Password });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("account.admin_cannot_delete", (await ReadAsync(response)).GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task An_Anonymous_Caller_Gets_401()
    {
        var response = await DeleteAsync(Client(), new { password = Password });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_Passwordless_Account_Confirms_With_Its_Email()
    {
        var (_, email, token) = await SeedUserAsync(passwordHash: string.Empty);

        var wrong = await DeleteAsync(Client(token), new { email = "other@deletion.local" });
        var right = await DeleteAsync(Client(token), new { email });

        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Equal("account.invalid_confirmation", (await ReadAsync(wrong)).GetProperty("errorCode").GetString());
        Assert.Equal(HttpStatusCode.NoContent, right.StatusCode);
    }

    [Fact]
    public async Task Open_Rentals_Are_409_With_The_Documented_Body_And_Nothing_Changes()
    {
        var (userId, _, token) = await SeedUserAsync();
        var ownerId = Guid.NewGuid();
        await _factory.SeedAsync(TestData.OwnerWithHome(ownerId, $"{ownerId:N}@deletion-owner.local"));
        var (listingId, _) = await SeedOwnerListingAsync(ownerId);
        var imageId = Guid.NewGuid();
        await _factory.SeedAsync(TestData.Image(imageId, listingId, isPrimary: true, sortOrder: 0));
        var bookingId = Guid.NewGuid();
        var start = TestData.Today.AddDays(10);
        await _factory.SeedAsync(TestData.Booking(bookingId, listingId, userId, start, start.AddDays(2), BookingStatus.Approved));
        var client = Client(token);

        var response = await DeleteAsync(client, new { password = Password });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        var body = await ReadAsync(response);
        Assert.Equal("account.has_open_rentals", body.GetProperty("errorCode").GetString());
        var rental = Assert.Single(body.GetProperty("openRentals").EnumerateArray());
        Assert.Equal(bookingId, rental.GetProperty("bookingId").GetGuid());
        Assert.Equal("LEGO Duplo Starter Set", rental.GetProperty("listingTitle").GetString());
        Assert.Equal($"/uploads/listings/{listingId:N}/{imageId:N}.png", rental.GetProperty("listingImageUrl").GetString());
        Assert.Equal(start.ToString("yyyy-MM-dd"), rental.GetProperty("startDate").GetString());
        Assert.Equal(start.AddDays(2).ToString("yyyy-MM-dd"), rental.GetProperty("endDate").GetString());
        Assert.Equal("renter", rental.GetProperty("role").GetString());
        Assert.Equal(6, rental.EnumerateObject().Count()); // exactly the six documented members, nothing extra
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task A_Blocked_User_Can_Still_Delete_Their_Account()
    {
        var (_, _, token) = await SeedUserAsync(blocked: true);

        var response = await DeleteAsync(Client(token), new { password = Password });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task The_Counterparty_Can_Still_Read_The_Booking_And_The_Deleted_Name_Is_Empty()
    {
        var (renterId, _, renterToken) = await SeedUserAsync();
        var ownerId = Guid.NewGuid();
        var ownerEmail = $"{ownerId:N}@deletion-owner.local";
        await _factory.SeedAsync(TestData.OwnerWithHome(ownerId, ownerEmail));
        var (listingId, _) = await SeedOwnerListingAsync(ownerId);
        var bookingId = Guid.NewGuid();
        await _factory.SeedAsync(TestData.Booking(
            bookingId, listingId, renterId, TestData.Today.AddDays(-10), TestData.Today.AddDays(-8), BookingStatus.Completed));
        Assert.Equal(HttpStatusCode.NoContent, (await DeleteAsync(Client(renterToken), new { password = Password })).StatusCode);

        var read = await Client(TestJwtTokenHelper.GenerateToken(ownerId, ownerEmail)).GetAsync($"/api/bookings/{bookingId}");

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var body = await ReadAsync(read);
        Assert.Equal("completed", body.GetProperty("status").GetString(), ignoreCase: true);
        Assert.Equal(renterId, body.GetProperty("counterpartyId").GetGuid());
        Assert.Equal(string.Empty, body.GetProperty("counterpartyFirstName").GetString());
        Assert.Equal(string.Empty, body.GetProperty("counterpartyLastName").GetString());
    }

    [Fact]
    public async Task Me_Reports_HasPassword_True_For_A_Password_Account_And_False_For_A_Google_Only_One()
    {
        var withPassword = await SeedUserAsync();
        var googleOnly = await SeedUserAsync(passwordHash: string.Empty);

        var first = await ReadAsync(await Client(withPassword.Token).GetAsync("/api/auth/me"));
        var second = await ReadAsync(await Client(googleOnly.Token).GetAsync("/api/auth/me"));

        Assert.True(first.GetProperty("hasPassword").GetBoolean());
        Assert.False(second.GetProperty("hasPassword").GetBoolean());
    }
}
