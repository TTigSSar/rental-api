using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Api;

// The HTTP contract of ADR-028 through the real pipeline (routing, model validation, rate limiting,
// ProblemDetails mapping, JSON casing) with the capture-fake transport reading back the emailed link.
//
// Every test pins a unique simulated client IP: the `auth` policy allows 5 requests/minute per IP and
// all classes in the "Integration" collection share one host.
[Collection("Integration")]
public sealed class EmailVerificationHttpTests
{
    private const string Password = "Sufficient1Password";

    private static int _ipCounter = 100;

    private readonly RentalPlatformWebAppFactory _factory;

    public EmailVerificationHttpTests(RentalPlatformWebAppFactory factory) => _factory = factory;

    private static string NextIp() => $"10.88.0.{Interlocked.Increment(ref _ipCounter)}";

    private HttpClient NewClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestRemoteIpStartupFilter.HeaderName, NextIp());
        return client;
    }

    private static string NewEmail() => $"user-{Guid.NewGuid():N}@http-verify.test";

    private static object RegisterBody(string email, string password = Password) => new
    {
        email,
        password,
        firstName = "Http",
        lastName = "Tester",
        phoneNumber = "+374 99 123456",
        preferredLanguage = "en"
    };

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private static string ErrorCode(JsonElement problem) => problem.GetProperty("errorCode").GetString()!;

    // ---- Register ----

    [Fact]
    public async Task Register_Returns_201_With_Email_And_VerificationRequired_And_No_Token()
    {
        var client = NewClient();
        var email = NewEmail();

        var response = await client.PostAsJsonAsync("/api/auth/register", RegisterBody(email));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal(email, body.GetProperty("email").GetString());
        Assert.True(body.GetProperty("verificationRequired").GetBoolean());
        Assert.False(body.TryGetProperty("accessToken", out _));
        Assert.False(body.TryGetProperty("user", out _));
        Assert.NotNull(_factory.EmailSender.LastTokenFor(email));
    }

    [Fact]
    public async Task Register_Twice_In_A_Row_Is_429_With_Retry_After_And_The_Problem_Code()
    {
        var client = NewClient();
        var email = NewEmail();
        await client.PostAsJsonAsync("/api/auth/register", RegisterBody(email));

        var second = await client.PostAsJsonAsync("/api/auth/register", RegisterBody(email, "AnotherPassword2"));

        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.Equal("auth.verification_cooldown", ErrorCode(await JsonAsync(second)));
        var retryAfter = Assert.Single(second.Headers.GetValues("Retry-After"));
        Assert.InRange(int.Parse(retryAfter), 1, 60);
    }

    [Fact]
    public async Task Register_For_A_Verified_Email_Is_409_Duplicate()
    {
        var client = NewClient();
        var email = NewEmail();
        await _factory.SeedAsync(TestData.User(Guid.NewGuid(), email));

        var response = await client.PostAsJsonAsync("/api/auth/register", RegisterBody(email));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("auth.duplicate_email", ErrorCode(await JsonAsync(response)));
    }

    [Fact]
    public async Task Register_Is_503_And_Creates_Nothing_While_The_Production_Gate_Is_Closed()
    {
        var closed = new FakeEmailVerificationSettings { IsOperational = false };
        using var gated = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IEmailVerificationSettings>();
            services.AddSingleton<IEmailVerificationSettings>(closed);
        }));
        var client = gated.CreateClient();
        client.DefaultRequestHeaders.Add(TestRemoteIpStartupFilter.HeaderName, NextIp());
        var email = NewEmail();

        var register = await client.PostAsJsonAsync("/api/auth/register", RegisterBody(email));
        var resend = await client.PostAsJsonAsync("/api/auth/resend-verification", new { email });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, register.StatusCode);
        Assert.Equal("auth.registration_unavailable", ErrorCode(await JsonAsync(register)));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, resend.StatusCode);
        Assert.Null(_factory.EmailSender.LastTokenFor(email));
    }

    // ---- The whole journey ----

    [Fact]
    public async Task Register_Then_Login_Is_403_Then_Verify_Signs_In_Then_Login_Works()
    {
        var client = NewClient();
        var email = NewEmail();
        await client.PostAsJsonAsync("/api/auth/register", RegisterBody(email));

        var blocked = await client.PostAsJsonAsync("/api/auth/login", new { email, password = Password });
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        Assert.Equal("auth.email_not_verified", ErrorCode(await JsonAsync(blocked)));

        var token = _factory.EmailSender.LastTokenFor(email)!;
        var verified = await client.PostAsJsonAsync("/api/auth/verify-email", new { token, password = Password });
        Assert.Equal(HttpStatusCode.OK, verified.StatusCode);
        var auth = await JsonAsync(verified);
        var accessToken = auth.GetProperty("accessToken").GetString();
        Assert.False(string.IsNullOrWhiteSpace(accessToken));
        Assert.Equal(email, auth.GetProperty("user").GetProperty("email").GetString());

        var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        // The JWT from verify-email really is a session.
        var me = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        me.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(me)).StatusCode);

        var again = await client.PostAsJsonAsync("/api/auth/verify-email", new { token, password = Password });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("auth.email_already_verified", ErrorCode(await JsonAsync(again)));
    }

    [Fact]
    public async Task Login_With_A_Wrong_Password_Is_401_Not_The_Verification_403()
    {
        var client = NewClient();
        var email = NewEmail();
        await client.PostAsJsonAsync("/api/auth/register", RegisterBody(email));

        var response = await client.PostAsJsonAsync("/api/auth/login", new { email, password = "WrongPassword9" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Verify_With_A_Wrong_Password_Is_401_And_The_Link_Still_Works()
    {
        var client = NewClient();
        var email = NewEmail();
        await client.PostAsJsonAsync("/api/auth/register", RegisterBody(email));
        var token = _factory.EmailSender.LastTokenFor(email)!;

        var wrong = await client.PostAsJsonAsync("/api/auth/verify-email", new { token, password = "WrongPassword9" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal("auth.invalid_credentials", ErrorCode(await JsonAsync(wrong)));

        var right = await client.PostAsJsonAsync("/api/auth/verify-email", new { token, password = Password });
        Assert.Equal(HttpStatusCode.OK, right.StatusCode);
    }

    [Fact]
    public async Task Verify_With_An_Unknown_Token_Is_400_Invalid()
    {
        var client = NewClient();

        var response = await client.PostAsJsonAsync("/api/auth/verify-email", new { token = "does-not-exist", password = Password });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("auth.verification_token_invalid", ErrorCode(await JsonAsync(response)));
    }

    [Fact]
    public async Task Verify_Without_A_Token_Or_Password_Fails_Model_Validation()
    {
        var client = NewClient();

        var response = await client.PostAsJsonAsync("/api/auth/verify-email", new { token = "", password = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- Resend ----

    [Fact]
    public async Task Resend_Is_202_With_No_Body_For_Unknown_Verified_And_Pending_Emails_Alike()
    {
        var client = NewClient();
        var verified = NewEmail();
        await _factory.SeedAsync(TestData.User(Guid.NewGuid(), verified));
        var pending = NewEmail();
        await client.PostAsJsonAsync("/api/auth/register", RegisterBody(pending));

        foreach (var email in new[] { NewEmail(), verified, pending })
        {
            var response = await client.PostAsJsonAsync("/api/auth/resend-verification", new { email });

            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
        }

        // Only the registration's own email: the pending one was inside its cooldown, the rest unsendable.
        Assert.Equal(1, _factory.EmailSender.CountFor(pending));
        Assert.Equal(0, _factory.EmailSender.CountFor(verified));
    }

    // ---- Rate limiting ----

    [Fact]
    public async Task Verify_And_Resend_Share_One_Per_Ip_Policy_That_Returns_429_After_Its_Limit()
    {
        const int permitLimit = 10; // RateLimiterExtensions.EmailVerificationPolicy

        var client = NewClient();

        for (var i = 0; i < permitLimit; i++)
        {
            var response = i % 2 == 0
                ? await client.PostAsJsonAsync("/api/auth/verify-email", new { token = "x", password = Password })
                : await client.PostAsJsonAsync("/api/auth/resend-verification", new { email = NewEmail() });

            Assert.NotEqual(HttpStatusCode.TooManyRequests, response.StatusCode);
        }

        var over = await client.PostAsJsonAsync("/api/auth/resend-verification", new { email = NewEmail() });
        Assert.Equal(HttpStatusCode.TooManyRequests, over.StatusCode);

        // A different client is unaffected: the limit is per IP, never global (ADR-028 section 8).
        var other = NewClient();
        var unaffected = await other.PostAsJsonAsync("/api/auth/resend-verification", new { email = NewEmail() });
        Assert.Equal(HttpStatusCode.Accepted, unaffected.StatusCode);
    }
}
