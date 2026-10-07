using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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

    // ---- GET /api/districts/at ------------------------------------------------------------------
    //
    // The live readout under the map pin. Anonymous BY DESIGN: the sign-up wizard asks this question
    // before the account it belongs to exists, so requiring a token would make the very first step
    // of registration impossible. Nothing sensitive is returned — the caller supplies the
    // coordinate and district boundaries are public OSM data.
    //
    // A non-null district is exactly "this point is saveable": the write side accepts precisely the
    // points this endpoint names a district for (IHomePointService.ValidateForSave). The pair of
    // tests at the bottom of this section pins that agreement, because a drift between them would
    // show the user a green district name for a pin the save then refuses.

    [Fact]
    public async Task GetDistrictAt_Inside_Yerevan_Returns_The_District_Anonymously()
    {
        var client = _factory.CreateClient();
        var (latitude, longitude) = TestData.KentronPoint;

        var response = await client.GetAsync($"/api/districts/at?lat={latitude}&lng={longitude}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<DistrictAtResponse>();

        Assert.NotNull(result);
        Assert.NotNull(result!.District);
        Assert.Equal("kentron", result.District!.Code);
        Assert.False(string.IsNullOrWhiteSpace(result.District.NameHy));
    }

    // Anywhere outside the 12 districts is a 200 with a null district — reporting that is this
    // endpoint's whole job, so it must never be an error. The client blocks on the null.
    [Theory]
    [InlineData(40.7850, 43.8453)]   // Gyumri — in Armenia, but not Yerevan
    [InlineData(51.5074, -0.1278)]   // London — not even close
    public async Task GetDistrictAt_Outside_Yerevan_Returns_A_Null_District(double latitude, double longitude)
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync($"/api/districts/at?lat={latitude}&lng={longitude}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<DistrictAtResponse>();

        Assert.NotNull(result);
        Assert.Null(result!.District);
    }

    // The response carries nothing but the district. An earlier revision also returned an
    // `inArmenia` flag; it was removed when Yerevan became the only saveable area, because a client
    // could only have used it to treat "in Armenia but outside Yerevan" as acceptable — which it is
    // not. This pins the shape so the field cannot quietly come back.
    [Fact]
    public async Task GetDistrictAt_Response_Carries_Only_The_District()
    {
        var client = _factory.CreateClient();
        var (latitude, longitude) = TestData.KentronPoint;

        var response = await client.GetAsync($"/api/districts/at?lat={latitude}&lng={longitude}");
        var json = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("inArmenia", json, StringComparison.OrdinalIgnoreCase);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(new[] { "district" }, doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
    }

    [Theory]
    [InlineData("lat=95&lng=44.5")]     // outside WGS84 latitude range
    [InlineData("lat=40.18&lng=200")]   // outside WGS84 longitude range
    [InlineData("lat=40.18")]           // longitude missing
    [InlineData("")]                    // both missing
    public async Task GetDistrictAt_Returns_400_For_A_Malformed_Coordinate(string query)
    {
        var response = await _factory.CreateClient().GetAsync($"/api/districts/at?{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
