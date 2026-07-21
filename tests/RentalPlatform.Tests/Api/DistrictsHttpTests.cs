using System.Net;
using System.Net.Http.Json;
using RentalPlatform.Application.DTOs;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Api;

[Collection("Integration")]
public sealed class DistrictsHttpTests
{
    private readonly RentalPlatformWebAppFactory _factory;

    public DistrictsHttpTests(RentalPlatformWebAppFactory factory) => _factory = factory;

    [Fact]
    public async Task GetDistricts_Returns_200_Anonymously_With_All_12_Districts_Populated()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/districts");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var districts = await response.Content.ReadFromJsonAsync<List<ListingDistrictResponse>>();

        Assert.NotNull(districts);
        Assert.Equal(12, districts!.Count);
        Assert.All(districts, district =>
        {
            Assert.NotEqual(Guid.Empty, district.Id);
            Assert.False(string.IsNullOrWhiteSpace(district.Code));
            Assert.False(string.IsNullOrWhiteSpace(district.NameEn));
            Assert.False(string.IsNullOrWhiteSpace(district.NameHy));
            Assert.False(string.IsNullOrWhiteSpace(district.NameRu));
        });
    }
}
