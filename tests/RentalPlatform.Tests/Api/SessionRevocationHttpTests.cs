using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RentalPlatform.Api.Hubs;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Infrastructure.Persistence;
using RentalPlatform.Infrastructure.Services;
using RentalPlatform.Tests.TestSupport;
using Xunit;
using static RentalPlatform.Tests.TestSupport.SessionRevocationTestSupport;

namespace RentalPlatform.Tests.Api;

// ADR-055 through the REAL JWT pipeline (real signature validation, real OnTokenValidated, real
// security-state cache and reader, real stores); no fake authentication handler (M-013). Tokens are
// issued by the real JwtTokenService or, where a test needs one the service would never issue, signed
// with the same key by TestJwtTokenHelper. Each test pins its own simulated client IP because the
// auth and password-change buckets are per IP and the whole "Integration" collection shares one host.
[Collection("Integration")]
public sealed class SessionRevocationHttpTests
{
    private const string OldPassword = "OldPassword123";
    private const string NewPassword = "BrandNewPassword9";

    private static int _ipCounter = 10;

    private readonly RentalPlatformWebAppFactory _factory;

    public SessionRevocationHttpTests(RentalPlatformWebAppFactory factory) => _factory = factory;

    private static string NextIp() => $"10.55.0.{Interlocked.Increment(ref _ipCounter)}";

    private static HttpClient Pin(HttpClient client)
    {
        client.DefaultRequestHeaders.Add(TestRemoteIpStartupFilter.HeaderName, NextIp());
        return client;
    }

    private static HttpClient WithToken(HttpClient client, string token)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<HttpStatusCode> MeAsync(HttpClient anonymousClient, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (await anonymousClient.SendAsync(request)).StatusCode;
    }

    private async Task<(Guid Id, string Email)> SeedUserAsync(int tokenVersion = 0)
    {
        var id = Guid.NewGuid();
        var email = $"{id:N}@revocation.local";
        var user = TestData.User(id, email, passwordHash: BCrypt.Net.BCrypt.HashPassword(OldPassword));
        user.TokenVersion = tokenVersion;
        await _factory.SeedAsync(user);
        return (id, email);
    }

    private static async Task<string> TokenFromAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("accessToken").GetString()!;
    }

    [Fact]
    public async Task A_Token_Without_The_Version_Claim_Is_401_Even_For_A_Valid_Active_User()
    {
        var (id, email) = await SeedUserAsync();
        var legacy = TestJwtTokenHelper.GenerateToken(id, email, tokenVersion: null);

        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(_factory.CreateClient(), legacy));
        Assert.Equal(HttpStatusCode.OK, await MeAsync(_factory.CreateClient(), TestJwtTokenHelper.GenerateToken(id, email)));
    }

    [Fact]
    public async Task A_Token_With_A_Different_Or_Unparseable_Version_Is_401()
    {
        var (id, email) = await SeedUserAsync(tokenVersion: 3);

        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(_factory.CreateClient(), TestJwtTokenHelper.GenerateToken(id, email, tokenVersion: 2)));
        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(_factory.CreateClient(), TestJwtTokenHelper.GenerateToken(id, email, tokenVersion: 4)));
        Assert.Equal(HttpStatusCode.OK, await MeAsync(_factory.CreateClient(), TestJwtTokenHelper.GenerateToken(id, email, tokenVersion: 3)));
    }

    [Fact]
    public async Task A_Token_For_A_User_That_Does_Not_Exist_Is_401()
    {
        var ghost = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.Unauthorized,
            await MeAsync(_factory.CreateClient(), TestJwtTokenHelper.GenerateToken(ghost, $"{ghost:N}@ghost.local")));
    }

    [Fact]
    public async Task A_Password_Change_Ends_Every_Other_Session_And_Returns_A_Fresh_One()
    {
        var (id, _) = await SeedUserAsync();
        var oldToken = TokenFor(id, 0);
        var other = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, await MeAsync(other, oldToken)); // warms the cache with the old version

        var put = await WithToken(Pin(_factory.CreateClient()), oldToken).PutAsJsonAsync("/api/auth/me/password", new
        {
            currentPassword = OldPassword,
            newPassword = NewPassword
        });

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var newToken = await TokenFromAsync(put);
        Assert.Equal(1, TokenVersionClaim(newToken));
        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(other, oldToken));   // takes effect at once, not after 60 s
        Assert.Equal(HttpStatusCode.OK, await MeAsync(other, newToken));
    }

    [Fact]
    public async Task A_Failing_Hub_Abort_Does_Not_Fail_The_Password_Change()
    {
        var (id, _) = await SeedUserAsync();
        var oldToken = TokenFor(id, 0);
        var broken = new FakeHubCallerContext(id, "broken-connection", throwOnAbort: true);
        _factory.Services.GetRequiredService<ChatConnectionRegistry>().Register(id, broken);

        var put = await WithToken(Pin(_factory.CreateClient()), oldToken).PutAsJsonAsync("/api/auth/me/password", new
        {
            currentPassword = OldPassword,
            newPassword = NewPassword
        });

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal(1, broken.AbortCount); // the abort was attempted and threw; nobody noticed
        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(_factory.CreateClient(), oldToken));
        await using var scope = _factory.Services.CreateAsyncScope();
        var stored = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.AsNoTracking().SingleAsync(user => user.Id == id);
        Assert.True(BCrypt.Net.BCrypt.Verify(NewPassword, stored.PasswordHash));
        Assert.Equal(1, stored.TokenVersion);
    }

    [Fact]
    public async Task A_Password_Reset_Ends_Older_Sessions_And_Returns_A_Fresh_One()
    {
        var (id, email) = await SeedUserAsync();
        var oldToken = TokenFor(id, 0);
        var other = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, await MeAsync(other, oldToken));

        var client = Pin(_factory.CreateClient());
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync("/api/auth/forgot-password", new { email })).StatusCode);
        var link = _factory.EmailSender.LastTokenFor(email)!;
        var reset = await client.PostAsJsonAsync("/api/auth/reset-password", new { token = link, newPassword = NewPassword });

        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        var newToken = await TokenFromAsync(reset);
        Assert.Equal(1, TokenVersionClaim(newToken));
        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(other, oldToken));
        Assert.Equal(HttpStatusCode.OK, await MeAsync(other, newToken));
    }

    [Fact]
    public async Task A_Suspension_Ends_The_Session_At_Once_And_A_Reactivated_User_Signs_In_And_Works_Immediately()
    {
        var (id, email) = await SeedUserAsync();
        var adminId = Guid.NewGuid();
        var adminEmail = $"{adminId:N}@revocation-admin.local";
        await _factory.SeedAsync(TestData.User(adminId, adminEmail, role: UserRole.Admin, isIdConfirmed: true));
        var admin = WithToken(_factory.CreateClient(), TestJwtTokenHelper.GenerateToken(adminId, adminEmail, UserRole.Admin));
        var userToken = TokenFor(id, 0);
        var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, await MeAsync(anonymous, userToken));

        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/admin/users/{id}/suspend", null)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(anonymous, userToken));
        // The blocked state is now cached; it must not outlive the reactivation.
        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(anonymous, TokenFor(id, 1)));

        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/admin/users/{id}/reactivate", null)).StatusCode);

        var login = await Pin(_factory.CreateClient()).PostAsJsonAsync("/api/auth/login", new { email, password = OldPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var freshToken = await TokenFromAsync(login);
        Assert.Equal(1, TokenVersionClaim(freshToken));
        Assert.Equal(HttpStatusCode.OK, await MeAsync(anonymous, freshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(anonymous, userToken)); // the pre-block session stays dead
    }

    [Fact]
    public async Task When_The_Session_Check_Cannot_Read_The_Database_An_Authenticated_Route_Answers_503_Not_401()
    {
        var (id, email) = await SeedUserAsync();
        using var broken = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IUserSecurityStateReader>();
            services.AddSingleton<IUserSecurityStateReader, ThrowingReader>();
        }));
        var token = TestJwtTokenHelper.GenerateToken(id, email);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await broken.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("auth.session_check_unavailable", doc.RootElement.GetProperty("errorCode").GetString());
    }

    // Documented behaviour: on a route that allows anonymous callers, a token whose session could not be
    // verified is simply not authenticated (the request proceeds as anonymous), never as the user.
    [Fact]
    public async Task When_The_Session_Check_Fails_An_Anonymous_Allowed_Route_Proceeds_Without_The_Identity()
    {
        var (id, email) = await SeedUserAsync();
        using var broken = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IUserSecurityStateReader>();
            services.AddSingleton<IUserSecurityStateReader, ThrowingReader>();
        }));

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/categories");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestJwtTokenHelper.GenerateToken(id, email));
        var response = await broken.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
