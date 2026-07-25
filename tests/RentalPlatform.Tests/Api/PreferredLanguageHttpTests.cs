using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Api;

// HTTP contract tests for PUT /api/auth/me/preferred-language. AuthServiceTests
// (Auth/AuthServiceTests.cs) already covers the business rules over hand-rolled fakes
// (normalization, invalid code, null/empty clears, unauthenticated). This class proves
// the parts a fake IUserAuthStore cannot: real [Authorize] middleware gating (401
// anonymous), real ProblemDetails wire shape (status + errorCode) for the 400/403 paths,
// and — the one no unit test can fake away — that a PUT actually persists through a real
// DbContext/SaveChangesAsync round trip, verified by a follow-up GET /api/auth/me on a
// fresh request rather than trusting the in-memory entity the PUT handler mutated.
[Collection("Integration")]
public sealed class PreferredLanguageHttpTests
{
    private readonly RentalPlatformWebAppFactory _factory;

    public PreferredLanguageHttpTests(RentalPlatformWebAppFactory factory) => _factory = factory;

    private async Task<Guid> SeedUserAsync(bool isBlocked = false)
    {
        var userId = Guid.NewGuid();
        await _factory.SeedAsync(TestData.User(userId, $"{userId:N}@lang.local", isBlocked));
        return userId;
    }

    private HttpClient ClientFor(Guid userId)
    {
        var token = TestJwtTokenHelper.GenerateToken(userId, $"{userId:N}@lang.local");
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task Put_Valid_Language_Returns_200_And_Survives_A_Fresh_GET()
    {
        var userId = await SeedUserAsync();
        var client = ClientFor(userId);

        var putResponse = await client.PutAsJsonAsync("/api/auth/me/preferred-language", new { preferredLanguage = "HY" });

        Assert.Equal(HttpStatusCode.OK, putResponse.StatusCode);
        using (var putDoc = JsonDocument.Parse(await putResponse.Content.ReadAsStringAsync()))
        {
            Assert.Equal("hy", putDoc.RootElement.GetProperty("preferredLanguage").GetString());
        }

        // Real persistence proof: a brand-new request round-trips through SaveChangesAsync
        // and the DbContext again, not just the entity the PUT handler happened to mutate.
        var getResponse = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        using var getDoc = JsonDocument.Parse(await getResponse.Content.ReadAsStringAsync());
        Assert.Equal("hy", getDoc.RootElement.GetProperty("preferredLanguage").GetString());
    }

    [Fact]
    public async Task Put_Null_Clears_Preference_And_Survives_A_Fresh_GET()
    {
        var userId = await SeedUserAsync(); // seeded with PreferredLanguage = "en"
        var client = ClientFor(userId);

        var putResponse = await client.PutAsJsonAsync("/api/auth/me/preferred-language", new { preferredLanguage = (string?)null });

        Assert.Equal(HttpStatusCode.OK, putResponse.StatusCode);

        var getResponse = await client.GetAsync("/api/auth/me");
        using var getDoc = JsonDocument.Parse(await getResponse.Content.ReadAsStringAsync());
        Assert.True(
            getDoc.RootElement.GetProperty("preferredLanguage").ValueKind is JsonValueKind.Null,
            $"expected null, got: {getDoc.RootElement.GetProperty("preferredLanguage")}");
    }

    [Fact]
    public async Task Put_Invalid_Language_Returns_400_With_Error_Code_And_Does_Not_Persist()
    {
        var userId = await SeedUserAsync(); // seeded with PreferredLanguage = "en"
        var client = ClientFor(userId);

        var putResponse = await client.PutAsJsonAsync("/api/auth/me/preferred-language", new { preferredLanguage = "fr" });

        Assert.Equal(HttpStatusCode.BadRequest, putResponse.StatusCode);
        using (var putDoc = JsonDocument.Parse(await putResponse.Content.ReadAsStringAsync()))
        {
            Assert.Equal("auth.invalid_language", putDoc.RootElement.GetProperty("errorCode").GetString());
        }

        var getResponse = await client.GetAsync("/api/auth/me");
        using var getDoc = JsonDocument.Parse(await getResponse.Content.ReadAsStringAsync());
        Assert.Equal("en", getDoc.RootElement.GetProperty("preferredLanguage").GetString());
    }

    [Fact]
    public async Task Put_Returns_401_When_Anonymous()
    {
        var response = await _factory.CreateClient()
            .PutAsJsonAsync("/api/auth/me/preferred-language", new { preferredLanguage = "en" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Put_Returns_403_For_Blocked_User()
    {
        var userId = await SeedUserAsync(isBlocked: true);
        var client = ClientFor(userId);

        var response = await client.PutAsJsonAsync("/api/auth/me/preferred-language", new { preferredLanguage = "ru" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
