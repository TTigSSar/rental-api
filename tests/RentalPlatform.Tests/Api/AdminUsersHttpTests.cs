using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Api;

// HTTP contract tests for AdminUsersController's suspend endpoint. Phase 2 (moderation messages)
// added an optional { reason?: string } request body — this covers that the additive change is
// backward compatible: a caller posting no body at all (the pre-existing shape) must still bind
// and succeed.
[Collection("Integration")]
public sealed class AdminUsersHttpTests
{
    private readonly RentalPlatformWebAppFactory _factory;

    public AdminUsersHttpTests(RentalPlatformWebAppFactory factory) => _factory = factory;

    private HttpClient ClientFor(Guid userId, string email, UserRole role = UserRole.User)
    {
        var token = TestJwtTokenHelper.GenerateToken(userId, email, role);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task Suspend_With_No_Request_Body_Still_Binds_And_Succeeds()
    {
        var adminId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        await _factory.SeedAsync(
            TestData.User(adminId, $"{adminId:N}@admin-suspend-nobody.local", role: UserRole.Admin, isIdConfirmed: true),
            TestData.User(targetId, $"{targetId:N}@admin-suspend-nobody.local"));

        var client = ClientFor(adminId, $"{adminId:N}@admin-suspend-nobody.local", UserRole.Admin);

        // No Content at all — the pre-existing caller shape this change must not break.
        var response = await client.PostAsync($"/api/admin/users/{targetId}/suspend", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Suspended", doc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Suspend_With_Reason_Body_Binds_And_Succeeds()
    {
        var adminId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        await _factory.SeedAsync(
            TestData.User(adminId, $"{adminId:N}@admin-suspend-reason.local", role: UserRole.Admin, isIdConfirmed: true),
            TestData.User(targetId, $"{targetId:N}@admin-suspend-reason.local"));

        var client = ClientFor(adminId, $"{adminId:N}@admin-suspend-reason.local", UserRole.Admin);

        var response = await client.PostAsJsonAsync(
            $"/api/admin/users/{targetId}/suspend", new { reason = "Repeated policy violations." });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
