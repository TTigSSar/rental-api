using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Api;

// HTTP contract tests for PUT /api/auth/me/password. AuthServiceTests (Auth/AuthServiceTests.cs)
// already covers the business rules over hand-rolled fakes (guard order, error codes, the
// real-hasher empty-hash and end-to-end cases). This class proves the parts a fake
// IUserAuthStore cannot: real [Authorize] middleware gating (401 anonymous), real
// ProblemDetails wire shape (status + errorCode) for the 400/403 paths, and — the one no unit
// test can fake away — that a password change actually persists through a real
// DbContext/SaveChangesAsync round trip, verified by a real POST /api/auth/login with the new
// and old passwords rather than trusting the in-memory entity the PUT handler mutated.
//
// NOTE on the shared "auth" rate-limit bucket: this class consumes exactly 2 of its 5
// permits-per-minute, both in Put_Valid_CurrentPassword_..._Returns_204 — the one assertion that
// genuinely needs a real POST /api/auth/login (proving the new password now authenticates AND
// the old one no longer does). Every other test either doesn't need a token at all, obtains one
// via TestJwtTokenHelper (no HTTP call, no bucket cost), or proves non-persistence via a second
// PUT /api/auth/me/password against the separate "password-change" bucket instead of a real
// login. RateLimitingTests (which also POSTs to /api/auth/login) no longer shares this bucket at
// all — see its own header comment.
[Collection("Integration")]
public sealed class ChangePasswordHttpTests
{
    private const string OldPassword = "OldPassword1";
    private const string NewPassword = "NewPassword2";

    private readonly RentalPlatformWebAppFactory _factory;

    public ChangePasswordHttpTests(RentalPlatformWebAppFactory factory) => _factory = factory;

    private async Task<(Guid UserId, string Email)> SeedUserAsync(bool isBlocked = false)
    {
        var userId = Guid.NewGuid();
        var email = $"{userId:N}@password.local";
        await _factory.SeedAsync(TestData.User(
            userId,
            email,
            isBlocked,
            passwordHash: BCrypt.Net.BCrypt.HashPassword(OldPassword)));
        return (userId, email);
    }

    private HttpClient ClientFor(Guid userId, string email)
    {
        var token = TestJwtTokenHelper.GenerateToken(userId, email);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task Put_Valid_CurrentPassword_Returns_204_And_New_Password_Replaces_Old_One_For_Login()
    {
        var (userId, email) = await SeedUserAsync();
        var client = ClientFor(userId, email);

        var putResponse = await client.PutAsJsonAsync("/api/auth/me/password", new
        {
            currentPassword = OldPassword,
            newPassword = NewPassword
        });

        Assert.Equal(HttpStatusCode.NoContent, putResponse.StatusCode);

        // Real persistence proof: brand-new anonymous requests round-trip through
        // SaveChangesAsync and the DbContext again, not just the entity the PUT handler
        // happened to mutate.
        var anonymousClient = _factory.CreateClient();

        var loginWithNewPassword = await anonymousClient.PostAsJsonAsync("/api/auth/login", new { email, password = NewPassword });
        Assert.Equal(HttpStatusCode.OK, loginWithNewPassword.StatusCode);

        var loginWithOldPassword = await anonymousClient.PostAsJsonAsync("/api/auth/login", new { email, password = OldPassword });
        Assert.Equal(HttpStatusCode.Unauthorized, loginWithOldPassword.StatusCode);
    }

    [Fact]
    public async Task Put_Wrong_Current_Password_Returns_400_With_Error_Code_And_Does_Not_Persist()
    {
        var (userId, email) = await SeedUserAsync();
        var client = ClientFor(userId, email);

        var putResponse = await client.PutAsJsonAsync("/api/auth/me/password", new
        {
            currentPassword = "WrongPassword1",
            newPassword = NewPassword
        });

        Assert.Equal(HttpStatusCode.BadRequest, putResponse.StatusCode);
        using (var putDoc = JsonDocument.Parse(await putResponse.Content.ReadAsStringAsync()))
        {
            Assert.Equal("auth.invalid_current_password", putDoc.RootElement.GetProperty("errorCode").GetString());
        }

        // Non-persistence proven via a second PUT (the "password-change" bucket) instead of a
        // real login against the shared "auth" bucket: if the wrong attempt above had somehow
        // persisted, OldPassword would no longer verify as the current password here either, and
        // this would come back 400 auth.invalid_current_password instead of 204.
        var confirmResponse = await client.PutAsJsonAsync("/api/auth/me/password", new
        {
            currentPassword = OldPassword,
            newPassword = NewPassword
        });
        Assert.Equal(HttpStatusCode.NoContent, confirmResponse.StatusCode);
    }

    [Fact]
    public async Task Put_Returns_401_When_Anonymous()
    {
        var response = await _factory.CreateClient()
            .PutAsJsonAsync("/api/auth/me/password", new { currentPassword = OldPassword, newPassword = NewPassword });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Put_Returns_403_For_Blocked_User()
    {
        var (userId, email) = await SeedUserAsync(isBlocked: true);
        var client = ClientFor(userId, email);

        var response = await client.PutAsJsonAsync("/api/auth/me/password", new
        {
            currentPassword = OldPassword,
            newPassword = NewPassword
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
