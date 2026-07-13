using System.Net;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Api;

[Collection("Integration")]
public sealed class HealthEndpointTests
{
    private readonly RentalPlatformWebAppFactory _factory;

    public HealthEndpointTests(RentalPlatformWebAppFactory factory) => _factory = factory;

    [Fact]
    public async Task Health_Returns_200_Anonymously_With_Status_Ok_Body()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"status\":\"ok\"", body);
    }
}
