using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Infrastructure.Services;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Api;

// The HTTP contract of ADR-035 through the real pipeline (routing, model validation, the shared
// `password-reset` rate limit, ProblemDetails + errorCode mapping, JSON casing) with the capture-fake
// transport reading back the emailed link. Every test pins a unique simulated client IP because the
// policy allows 5 requests/minute per IP and all classes in "Integration" share one host.
[Collection("Integration")]
public sealed class PasswordResetHttpTests
{
    private const string OldPassword = "OldPassword123";
    private const string NewPassword = "BrandNewPassword9";

    private static int _ipCounter = 100;

    private readonly RentalPlatformWebAppFactory _factory;

    public PasswordResetHttpTests(RentalPlatformWebAppFactory factory) => _factory = factory;

    private static string NextIp() => $"10.89.0.{Interlocked.Increment(ref _ipCounter)}";

    private HttpClient NewClient(RentalPlatformWebAppFactory? factory = null)
    {
        var client = (factory ?? _factory).CreateClient();
        client.DefaultRequestHeaders.Add(TestRemoteIpStartupFilter.HeaderName, NextIp());
        return client;
    }

    private static string NewEmail() => $"user-{Guid.NewGuid():N}@http-reset.test";

    private async Task<string> SeedVerifiedAsync(string? hash = null)
    {
        var email = NewEmail();
        await _factory.SeedAsync(TestData.User(
            Guid.NewGuid(), email, passwordHash: hash ?? new BcryptPasswordHasher().HashPassword(OldPassword)));
        return email;
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private static string ErrorCode(JsonElement problem) => problem.GetProperty("errorCode").GetString()!;

    [Fact]
    public async Task Forgot_Is_202_With_No_Body_For_Unknown_Verified_Pending_Blocked_And_Malformed_Alike()
    {
        var client = NewClient();
        var verified = await SeedVerifiedAsync();
        var blocked = NewEmail();
        await _factory.SeedAsync(TestData.User(Guid.NewGuid(), blocked, isBlocked: true));
        var pending = NewEmail();
        await _factory.SeedAsync(TestData.User(Guid.NewGuid(), pending, isEmailConfirmed: false));

        // 5 requests fit the per-IP bucket exactly.
        foreach (var email in new[] { NewEmail(), verified, pending, blocked, "not-an-email" })
        {
            var response = await client.PostAsJsonAsync("/api/auth/forgot-password", new { email, preferredLanguage = "en" });

            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
        }

        Assert.Equal(1, _factory.EmailSender.CountFor(verified));
        Assert.Equal(0, _factory.EmailSender.CountFor(pending));
        Assert.Equal(0, _factory.EmailSender.CountFor(blocked));
    }

    [Fact]
    public async Task The_Whole_Journey_Forgot_Check_Reset_Signs_In_And_The_New_Password_Logs_In()
    {
        var client = NewClient();
        var email = await SeedVerifiedAsync();

        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync("/api/auth/forgot-password", new { email, preferredLanguage = "hy" })).StatusCode);
        var message = _factory.EmailSender.Messages.Last(m => m.To == email);
        Assert.StartsWith("DoRent-ի", message.Subject, StringComparison.Ordinal); // the request language was sent and honoured
        var token = _factory.EmailSender.LastTokenFor(email)!;

        var check = await client.PostAsJsonAsync("/api/auth/reset-password/check", new { token });
        Assert.Equal(HttpStatusCode.NoContent, check.StatusCode);

        var reset = await client.PostAsJsonAsync("/api/auth/reset-password", new { token, newPassword = NewPassword });
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        var auth = await JsonAsync(reset);
        var accessToken = auth.GetProperty("accessToken").GetString();
        Assert.False(string.IsNullOrWhiteSpace(accessToken));
        Assert.Equal(email, auth.GetProperty("user").GetProperty("email").GetString());

        var me = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        me.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        Assert.Equal(HttpStatusCode.OK, (await NewClient().SendAsync(me)).StatusCode);

        var login = await NewClient().PostAsJsonAsync("/api/auth/login", new { email, password = NewPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [Fact]
    public async Task Check_And_Reset_Answer_400_With_Error_Codes_For_Dead_Links()
    {
        var client = NewClient();

        var check = await client.PostAsJsonAsync("/api/auth/reset-password/check", new { token = "does-not-exist" });
        var reset = await client.PostAsJsonAsync("/api/auth/reset-password", new { token = "does-not-exist", newPassword = NewPassword });

        Assert.Equal(HttpStatusCode.BadRequest, check.StatusCode);
        Assert.Equal("auth.reset_token_invalid", ErrorCode(await JsonAsync(check)));
        Assert.Equal(HttpStatusCode.BadRequest, reset.StatusCode);
        Assert.Equal("auth.reset_token_invalid", ErrorCode(await JsonAsync(reset)));
    }

    [Fact]
    public async Task Reset_With_A_Password_Over_72_Bytes_Is_400_With_The_Error_Code_And_Too_Short_Fails_Validation()
    {
        var client = NewClient();
        var email = await SeedVerifiedAsync();
        await client.PostAsJsonAsync("/api/auth/forgot-password", new { email });
        var token = _factory.EmailSender.LastTokenFor(email)!;

        var tooLong = await client.PostAsJsonAsync("/api/auth/reset-password", new { token, newPassword = new string('ա', 37) });
        var tooShort = await client.PostAsJsonAsync("/api/auth/reset-password", new { token, newPassword = "short" });

        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Equal("auth.password_too_long", ErrorCode(await JsonAsync(tooLong)));
        Assert.Equal(HttpStatusCode.BadRequest, tooShort.StatusCode);

        // Neither rejection spent the link.
        var ok = await client.PostAsJsonAsync("/api/auth/reset-password", new { token, newPassword = NewPassword });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [Fact]
    public async Task An_External_Only_User_Can_Reset_Then_Log_In_With_The_New_Password()
    {
        var client = NewClient();
        var email = await SeedVerifiedAsync(hash: string.Empty);
        await client.PostAsJsonAsync("/api/auth/forgot-password", new { email });
        var token = _factory.EmailSender.LastTokenFor(email)!;

        var reset = await client.PostAsJsonAsync("/api/auth/reset-password", new { token, newPassword = NewPassword });
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);

        var login = await NewClient().PostAsJsonAsync("/api/auth/login", new { email, password = NewPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [Fact]
    public async Task All_Three_Endpoints_Share_One_Per_Ip_Bucket_Of_Five_Per_Minute()
    {
        const int permitLimit = 5; // RateLimiterExtensions.PasswordResetPolicy

        var client = NewClient();
        var requests = new Func<Task<HttpResponseMessage>>[]
        {
            () => client.PostAsJsonAsync("/api/auth/forgot-password", new { email = NewEmail() }),
            () => client.PostAsJsonAsync("/api/auth/reset-password/check", new { token = "x" }),
            () => client.PostAsJsonAsync("/api/auth/reset-password", new { token = "x", newPassword = NewPassword }),
            () => client.PostAsJsonAsync("/api/auth/forgot-password", new { email = NewEmail() }),
            () => client.PostAsJsonAsync("/api/auth/reset-password/check", new { token = "x" })
        };

        for (var i = 0; i < permitLimit; i++)
        {
            Assert.NotEqual(HttpStatusCode.TooManyRequests, (await requests[i]()).StatusCode);
        }

        // The sixth, on any of the three, is limited...
        var over = await client.PostAsJsonAsync("/api/auth/reset-password", new { token = "x", newPassword = NewPassword });
        Assert.Equal(HttpStatusCode.TooManyRequests, over.StatusCode);

        // ...it is its own bucket (login, on the `auth` policy, is unaffected by it)...
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email = NewEmail(), password = "whatever123" });
        Assert.NotEqual(HttpStatusCode.TooManyRequests, login.StatusCode);

        // ...and per IP, never global.
        var other = await NewClient().PostAsJsonAsync("/api/auth/forgot-password", new { email = NewEmail() });
        Assert.Equal(HttpStatusCode.Accepted, other.StatusCode);
    }

    [Fact]
    public async Task Forgot_Is_503_With_The_Error_Code_While_The_Production_Gate_Is_Closed()
    {
        var closed = new FakeEmailVerificationSettings { IsOperational = false };
        using var gated = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IEmailVerificationSettings>();
            services.AddSingleton<IEmailVerificationSettings>(closed);
        }));
        var client = gated.CreateClient();
        client.DefaultRequestHeaders.Add(TestRemoteIpStartupFilter.HeaderName, NextIp());
        var email = await SeedVerifiedAsync();

        var response = await client.PostAsJsonAsync("/api/auth/forgot-password", new { email });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("auth.password_reset_unavailable", ErrorCode(await JsonAsync(response)));
        Assert.Null(_factory.EmailSender.LastTokenFor(email));
    }
}
