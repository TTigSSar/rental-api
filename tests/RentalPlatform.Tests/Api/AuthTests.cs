using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Api;

[Collection("Integration")]
public sealed class AuthTests
{
    private readonly RentalPlatformWebAppFactory _factory;

    public AuthTests(RentalPlatformWebAppFactory factory) => _factory = factory;

    [Fact]
    public async Task Unauthenticated_Request_To_Protected_Endpoint_Returns_401()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Valid_User_Token_Accesses_Protected_Endpoint()
    {
        var userId = Guid.NewGuid();
        var email = $"{userId:N}@auth-test.local";

        await _factory.SeedAsync(TestData.User(userId, email));

        var token = TestJwtTokenHelper.GenerateToken(userId, email);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Regular_User_Gets_403_On_Admin_Endpoint()
    {
        var userId = Guid.NewGuid();
        var email = $"{userId:N}@auth-test.local";

        await _factory.SeedAsync(TestData.User(userId, email));

        var token = TestJwtTokenHelper.GenerateToken(userId, email, UserRole.User);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync("/api/admin/listings/pending");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Admin_Token_Accesses_Admin_Endpoint()
    {
        var adminId = Guid.NewGuid();
        var email = $"{adminId:N}@auth-test.local";

        await _factory.SeedAsync(TestData.User(adminId, email, role: UserRole.Admin));

        var token = TestJwtTokenHelper.GenerateToken(adminId, email, UserRole.Admin);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync("/api/admin/listings/pending");

        // 200 OK — admin sees the (empty) pending list.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
