using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Api;

[Collection("Integration")]
public sealed class SecurityHeadersTests
{
    private readonly RentalPlatformWebAppFactory _factory;

    public SecurityHeadersTests(RentalPlatformWebAppFactory factory) => _factory = factory;

    [Fact]
    public async Task Every_Response_Carries_Security_Headers()
    {
        // GET /api/categories is anonymous and does no DB work beyond a simple query,
        // so it is the cheapest endpoint to hit for a header-only assertion.
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/categories");

        Assert.Equal("nosniff",
            response.Headers.GetValues("X-Content-Type-Options").Single());

        Assert.Equal("DENY",
            response.Headers.GetValues("X-Frame-Options").Single());

        Assert.Equal("strict-origin-when-cross-origin",
            response.Headers.GetValues("Referrer-Policy").Single());
    }

    [Fact]
    public async Task Error_Response_Also_Carries_Security_Headers()
    {
        // A 404 from routing should still go through SecurityHeadersMiddleware.
        var client = _factory.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false
            });

        var response = await client.GetAsync("/api/does-not-exist");

        Assert.Equal("nosniff",
            response.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Fact]
    public async Task Unauthenticated_401_Also_Carries_Security_Headers()
    {
        var client = _factory.CreateClient();
        // No Authorization header → 401 from JWT middleware.
        var response = await client.GetAsync("/api/auth/me");

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("nosniff",
            response.Headers.GetValues("X-Content-Type-Options").Single());
    }
}
