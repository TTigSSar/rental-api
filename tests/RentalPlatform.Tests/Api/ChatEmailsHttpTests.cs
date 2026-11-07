using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Infrastructure.Persistence;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Api;

// HTTP contract of ADR-059: GET /me carries chatEmailsEnabled, PUT /me/chat-emails persists it, and
// POST /chat-emails/unsubscribe (anonymous, signed token) turns it off. Through the real pipeline and a real
// DbContext round trip, verified in the database rather than through the entity the handler mutated.
[Collection("Integration")]
public sealed class ChatEmailsHttpTests
{
    private static int _ipCounter = 100;

    private readonly RentalPlatformWebAppFactory _factory;

    public ChatEmailsHttpTests(RentalPlatformWebAppFactory factory) => _factory = factory;

    private static string NextIp() => $"10.93.0.{Interlocked.Increment(ref _ipCounter)}";

    private async Task<Guid> SeedUserAsync(bool isBlocked = false)
    {
        var userId = Guid.NewGuid();
        await _factory.SeedAsync(TestData.User(userId, $"{userId:N}@chatmail.local", isBlocked));
        return userId;
    }

    private HttpClient ClientFor(Guid userId)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestJwtTokenHelper.GenerateToken(userId, $"{userId:N}@chatmail.local"));
        return client;
    }

    private HttpClient AnonymousClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestRemoteIpStartupFilter.HeaderName, NextIp());
        return client;
    }

    private async Task<bool> StoredEnabledAsync(Guid userId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return (await db.Users.AsNoTracking().SingleAsync(user => user.Id == userId)).ChatEmailsEnabled;
    }

    private string TokenFor(Guid userId) =>
        _factory.Services.GetRequiredService<IChatEmailUnsubscribeTokens>().Issue(userId);

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    [Fact]
    public async Task Me_Reports_Chat_Emails_On_By_Default()
    {
        var userId = await SeedUserAsync();

        var response = await ClientFor(userId).GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await JsonAsync(response)).GetProperty("chatEmailsEnabled").GetBoolean());
    }

    [Fact]
    public async Task Put_Toggles_The_Preference_And_It_Survives_A_Fresh_Request()
    {
        var userId = await SeedUserAsync();
        var client = ClientFor(userId);

        var off = await client.PutAsJsonAsync("/api/auth/me/chat-emails", new { enabled = false });
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        Assert.False((await JsonAsync(off)).GetProperty("chatEmailsEnabled").GetBoolean());
        Assert.False(await StoredEnabledAsync(userId));
        Assert.False((await JsonAsync(await client.GetAsync("/api/auth/me"))).GetProperty("chatEmailsEnabled").GetBoolean());

        var on = await client.PutAsJsonAsync("/api/auth/me/chat-emails", new { enabled = true });
        Assert.Equal(HttpStatusCode.OK, on.StatusCode);
        Assert.True(await StoredEnabledAsync(userId));
    }

    [Fact]
    public async Task Put_Without_The_Field_Is_400_And_Changes_Nothing()
    {
        var userId = await SeedUserAsync();

        var response = await ClientFor(userId).PutAsJsonAsync("/api/auth/me/chat-emails", new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(await StoredEnabledAsync(userId));
    }

    [Fact]
    public async Task Put_Is_401_Anonymous_And_403_For_A_Blocked_User()
    {
        var anonymous = await _factory.CreateClient().PutAsJsonAsync("/api/auth/me/chat-emails", new { enabled = false });
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var blockedId = await SeedUserAsync(isBlocked: true);
        var blocked = await ClientFor(blockedId).PutAsJsonAsync("/api/auth/me/chat-emails", new { enabled = false });
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        Assert.Equal("auth.user_blocked", (await JsonAsync(blocked)).GetProperty("errorCode").GetString());
        Assert.True(await StoredEnabledAsync(blockedId));
    }

    [Fact]
    public async Task Unsubscribe_Needs_No_Login_And_Reports_Unsubscribed_Then_Already_Off()
    {
        var userId = await SeedUserAsync();
        var token = TokenFor(userId);
        var client = AnonymousClient();

        var first = await client.PostAsJsonAsync("/api/auth/chat-emails/unsubscribe", new { token });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("unsubscribed", (await JsonAsync(first)).GetProperty("result").GetString());
        Assert.False(await StoredEnabledAsync(userId));

        var second = await client.PostAsJsonAsync("/api/auth/chat-emails/unsubscribe", new { token });
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal("alreadyOff", (await JsonAsync(second)).GetProperty("result").GetString());
    }

    [Fact]
    public async Task Unsubscribe_With_A_Bad_Token_Or_Unknown_User_Is_400_With_The_Error_Code()
    {
        var userId = await SeedUserAsync();
        var good = TokenFor(userId);
        var tampered = good[..^2] + (good[^2] == 'A' ? 'B' : 'A') + good[^1];
        var client = AnonymousClient();

        foreach (var token in new[] { tampered, "garbage", "", TokenFor(Guid.NewGuid()) })
        {
            var response = await client.PostAsJsonAsync("/api/auth/chat-emails/unsubscribe", new { token });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("chat_email.unsubscribe_invalid", (await JsonAsync(response)).GetProperty("errorCode").GetString());
        }

        Assert.True(await StoredEnabledAsync(userId));
    }

    [Fact]
    public async Task Unsubscribe_Is_Rate_Limited_Per_Ip()
    {
        var client = AnonymousClient();
        HttpStatusCode last = default;
        for (var i = 0; i < 7; i++)
        {
            last = (await client.PostAsJsonAsync("/api/auth/chat-emails/unsubscribe", new { token = "garbage" })).StatusCode;
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, last);
    }
}
