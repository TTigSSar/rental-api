using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using RentalPlatform.Infrastructure.Persistence;
using RentalPlatform.Infrastructure.Services;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Api;

// ADR-030 over the real pipeline: nonce issuance, the whole sign-in with the real validator and nonce
// store (only Google's signature check is stubbed), error mapping and the two rate-limit policies.
//
// Every test pins its own simulated client IP: both policies are per IP (30 and 10 per minute) and
// every class in the "Integration" collection shares one host.
[Collection("Integration")]
public sealed class GoogleSignInHttpTests
{
    private static int _ipCounter;

    private readonly RentalPlatformWebAppFactory _factory;

    public GoogleSignInHttpTests(RentalPlatformWebAppFactory factory) => _factory = factory;

    private static string NextIp()
    {
        var n = Interlocked.Increment(ref _ipCounter);
        return $"10.66.{(n >> 8) & 0xFF}.{n & 0xFF}";
    }

    private static HttpClient Pin(HttpClient client, string ip)
    {
        client.DefaultRequestHeaders.Add(TestRemoteIpStartupFilter.HeaderName, ip);
        return client;
    }

    private HttpClient NewClient(string? ip = null) => Pin(_factory.CreateClient(), ip ?? NextIp());

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private static string ErrorCode(JsonElement problem) => problem.GetProperty("errorCode").GetString()!;

    private static string NewEmail(string domain = "gmail.com") => $"g-{Guid.NewGuid():N}@{domain}";

    private async Task<string> GetNonceAsync(HttpClient client)
    {
        var response = await client.PostAsync("/api/auth/external/nonce", content: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await JsonAsync(response)).GetProperty("nonce").GetString()!;
    }

    // Registers the payload a token string stands for, with a nonce taken from the real endpoint.
    private async Task<string> SignedTokenAsync(HttpClient client, string email, string? sub = null, bool emailVerified = true,
        string? givenName = "Gina", string? familyName = "Google", string? name = "Gina Google")
    {
        var nonce = await GetNonceAsync(client);
        var token = $"token-{Guid.NewGuid():N}";
        _factory.GoogleVerifier.Register(token, nonce, subject: sub ?? $"sub-{Guid.NewGuid():N}", email: email,
            emailVerified: emailVerified, givenName: givenName, familyName: familyName, name: name);
        return token;
    }

    private static Task<HttpResponseMessage> SignInAsync(HttpClient client, string idToken, string? preferredLanguage = null) =>
        client.PostAsJsonAsync("/api/auth/external", new { provider = "google", idToken, preferredLanguage });

    private async Task<User1> StoredAsync(string email)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await db.Users.AsNoTracking().SingleAsync(candidate => candidate.Email == email);
        return new User1(user.Id, user.FirstName, user.LastName, user.PreferredLanguage, user.IsEmailConfirmed, user.ExternalAuthProvider);
    }

    private sealed record User1(Guid Id, string FirstName, string LastName, string? PreferredLanguage, bool IsEmailConfirmed, string? Provider);

    // ---- Nonce ----

    [Fact]
    public async Task Nonce_Returns_200_With_A_Fresh_Nonce_Expiring_In_About_Five_Minutes()
    {
        var client = NewClient();

        var first = await client.PostAsync("/api/auth/external/nonce", content: null);
        var second = await client.PostAsync("/api/auth/external/nonce", content: null);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var body = await JsonAsync(first);
        var nonce = body.GetProperty("nonce").GetString()!;
        Assert.Equal(43, nonce.Length);
        var expiresAtText = body.GetProperty("expiresAt").GetString()!;
        Assert.EndsWith("Z", expiresAtText); // ISO-8601 UTC
        var expiresAt = DateTimeOffset.Parse(expiresAtText, System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(expiresAt - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(5.1));
        Assert.NotEqual(nonce, (await JsonAsync(second)).GetProperty("nonce").GetString());
    }

    [Fact]
    public async Task Nonce_Needs_No_Authentication()
    {
        var response = await NewClient().PostAsync("/api/auth/external/nonce", content: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Nonce_Returns_429_On_The_31st_Request_From_One_Ip_And_Other_Ips_Are_Unaffected()
    {
        var ip = NextIp();
        var client = NewClient(ip);

        for (var i = 0; i < 30; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/auth/external/nonce", content: null)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsync("/api/auth/external/nonce", content: null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await NewClient().PostAsync("/api/auth/external/nonce", content: null)).StatusCode);
    }

    [Fact]
    public async Task Nonce_And_Sign_In_Have_Separate_Budgets()
    {
        // 10 sign-ins exhaust external-auth, yet the nonce endpoint on the same IP still answers 200.
        var client = NewClient();
        for (var i = 0; i < 10; i++)
        {
            await SignInAsync(client, "garbage");
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await SignInAsync(client, "garbage")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/auth/external/nonce", content: null)).StatusCode);
    }

    [Fact]
    public async Task Nonce_Rate_Limit_Shares_One_Budget_Across_An_IPv6_Slash48()
    {
        var suffix = Interlocked.Increment(ref _ipCounter);
        var hex = suffix.ToString("x");
        var a = NewClient($"2001:db8:{hex}:1::1");
        var b = NewClient($"2001:db8:{hex}:2::1"); // a different /64 of the same /48

        for (var i = 0; i < 30; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await a.PostAsync("/api/auth/external/nonce", content: null)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await b.PostAsync("/api/auth/external/nonce", content: null)).StatusCode);
    }

    private HttpClient UnconfiguredClient(out IDisposable host)
    {
        var unconfigured = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IOptions<ExternalAuthOptions>>();
            services.AddSingleton<IOptions<ExternalAuthOptions>>(Options.Create(new ExternalAuthOptions
            {
                Google = new GoogleExternalAuthOptions { ValidAudiences = new[] { "set-google-client-id-in-environment", "" } }
            }));
        }));
        host = unconfigured;
        return Pin(unconfigured.CreateClient(), NextIp());
    }

    [Fact]
    public async Task Nonce_And_Sign_In_Answer_503_When_Google_Is_Not_Configured()
    {
        var client = UnconfiguredClient(out var host);
        using var _ = host;

        var nonce = await client.PostAsync("/api/auth/external/nonce", content: null);
        var signIn = await SignInAsync(client, "anything");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, nonce.StatusCode);
        Assert.Equal("auth.external_provider_unavailable", ErrorCode(await JsonAsync(nonce)));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, signIn.StatusCode);
        Assert.Equal("auth.external_provider_unavailable", ErrorCode(await JsonAsync(signIn)));
    }

    // ---- Sign in ----

    [Fact]
    public async Task Sign_In_With_An_Issued_Nonce_Creates_The_User_And_Returns_A_Token_Once()
    {
        var client = NewClient();
        var email = NewEmail();
        var token = await SignedTokenAsync(client, email);

        var response = await SignInAsync(client, token, preferredLanguage: "hy");
        var replay = await SignInAsync(client, token, preferredLanguage: "hy");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.False(string.IsNullOrEmpty(body.GetProperty("accessToken").GetString()));
        Assert.Equal(email, body.GetProperty("user").GetProperty("email").GetString());
        var stored = await StoredAsync(email);
        Assert.Equal("hy", stored.PreferredLanguage);
        Assert.Equal("Gina", stored.FirstName);
        Assert.True(stored.IsEmailConfirmed);

        // The nonce is spent: the very same token cannot be replayed.
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        Assert.Equal("auth.external_invalid_token", ErrorCode(await JsonAsync(replay)));
    }

    [Theory]
    [InlineData("xx")]
    [InlineData("hy-AM")]
    [InlineData("")]
    public async Task An_Unsupported_Language_Is_Stored_As_Null_Not_Rejected(string language)
    {
        var client = NewClient();
        var email = NewEmail();

        var response = await SignInAsync(client, await SignedTokenAsync(client, email), preferredLanguage: language);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null((await StoredAsync(email)).PreferredLanguage);
    }

    [Fact]
    public async Task Sign_In_Without_The_Language_Field_Stores_Null()
    {
        var client = NewClient();
        var email = NewEmail();

        var response = await SignInAsync(client, await SignedTokenAsync(client, email));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null((await StoredAsync(email)).PreferredLanguage);
    }

    [Fact]
    public async Task A_Name_Less_Google_Account_Gets_An_Empty_First_Name_Never_The_Email()
    {
        var client = NewClient();
        var email = NewEmail();

        var response = await SignInAsync(client, await SignedTokenAsync(client, email, givenName: null, familyName: null, name: null));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var user = (await JsonAsync(response)).GetProperty("user");
        Assert.Equal(string.Empty, user.GetProperty("firstName").GetString());
        Assert.Equal(string.Empty, user.GetProperty("lastName").GetString());
    }

    [Fact]
    public async Task A_Garbage_Token_Is_400_And_Leaves_The_Nonce_Usable()
    {
        var client = NewClient();
        var nonce = await GetNonceAsync(client);

        var response = await SignInAsync(client, "garbage");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("auth.external_invalid_token", ErrorCode(await JsonAsync(response)));

        // The same nonce still works for a genuine token.
        _factory.GoogleVerifier.Register("real-after-garbage", nonce, subject: $"sub-{Guid.NewGuid():N}", email: NewEmail());
        Assert.Equal(HttpStatusCode.OK, (await SignInAsync(client, "real-after-garbage")).StatusCode);
    }

    [Fact]
    public async Task A_Valid_Token_With_A_Nonce_The_Server_Never_Issued_Is_400()
    {
        var client = NewClient();
        _factory.GoogleVerifier.Register("forged-nonce", "not-issued-by-us", subject: $"sub-{Guid.NewGuid():N}", email: NewEmail());

        var response = await SignInAsync(client, "forged-nonce");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("auth.external_invalid_token", ErrorCode(await JsonAsync(response)));
    }

    [Fact]
    public async Task An_Unverified_Google_Email_For_An_Unknown_Identity_Is_400_email_missing()
    {
        var client = NewClient();

        var response = await SignInAsync(client, await SignedTokenAsync(client, NewEmail(), emailVerified: false));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("auth.external_email_missing", ErrorCode(await JsonAsync(response)));
    }

    [Fact]
    public async Task An_Unsupported_Provider_Is_400_And_The_Message_Does_Not_Echo_It()
    {
        var response = await NewClient().PostAsJsonAsync("/api/auth/external", new { provider = "evilcorp", idToken = "x" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("auth.external_provider_unsupported", body, StringComparison.Ordinal);
        Assert.DoesNotContain("evilcorp", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Blocked_User_Is_403_And_Gets_No_Token()
    {
        var client = NewClient();
        var email = NewEmail();
        var sub = $"sub-{Guid.NewGuid():N}";
        var blocked = TestData.User(Guid.NewGuid(), email, isBlocked: true);
        blocked.ExternalAuthProvider = "google";
        blocked.ExternalProviderId = sub;
        await _factory.SeedAsync(blocked);

        var response = await SignInAsync(client, await SignedTokenAsync(client, email, sub));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("auth.user_blocked", ErrorCode(await JsonAsync(response)));
    }

    [Fact]
    public async Task A_Confirmed_Account_On_A_Non_Authoritative_Email_Is_409_link_conflict()
    {
        var client = NewClient();
        var email = NewEmail("yahoo.com");
        await _factory.SeedAsync(TestData.User(Guid.NewGuid(), email, passwordHash: "x"));

        var response = await SignInAsync(client, await SignedTokenAsync(client, email));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("auth.external_link_conflict", ErrorCode(await JsonAsync(response)));
    }

    [Fact]
    public async Task A_Pending_Registration_On_A_Non_Authoritative_Email_Is_409_pending_registration()
    {
        var client = NewClient();
        var email = NewEmail("yahoo.com");
        await _factory.SeedAsync(TestData.User(Guid.NewGuid(), email, passwordHash: "hashed:x", isEmailConfirmed: false));

        var response = await SignInAsync(client, await SignedTokenAsync(client, email));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("auth.external_pending_registration", ErrorCode(await JsonAsync(response)));
        Assert.False((await StoredAsync(email)).IsEmailConfirmed);
    }

    [Fact]
    public async Task A_Pending_Registration_On_A_Gmail_Address_Is_Converted_With_The_Requested_Language()
    {
        var client = NewClient();
        var email = NewEmail();
        await _factory.SeedAsync(TestData.User(Guid.NewGuid(), email, passwordHash: "hashed:x", isEmailConfirmed: false, preferredLanguage: "en"));

        var response = await SignInAsync(client, await SignedTokenAsync(client, email), preferredLanguage: "ru");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await StoredAsync(email);
        Assert.True(stored.IsEmailConfirmed);
        Assert.Equal("ru", stored.PreferredLanguage);
        Assert.Equal("google", stored.Provider);
    }

    [Fact]
    public async Task Sign_In_Returns_429_On_The_11th_Request_From_One_Ip()
    {
        var client = NewClient();

        for (var i = 0; i < 10; i++)
        {
            Assert.NotEqual(HttpStatusCode.TooManyRequests, (await SignInAsync(client, "garbage")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await SignInAsync(client, "garbage")).StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, (await SignInAsync(NewClient(), "garbage")).StatusCode);
    }
}
