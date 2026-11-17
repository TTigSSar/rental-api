using System.Net;
using System.Text.Json;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Api;

// ADR-066 over the real pipeline: repeated-key binding, [ApiController] 400s, the facets route.
[Collection("Integration")]
public sealed class BrowseFiltersV2HttpTests
{
    private readonly RentalPlatformWebAppFactory _factory;

    public BrowseFiltersV2HttpTests(RentalPlatformWebAppFactory factory) => _factory = factory;

    [Theory]
    [InlineData("/api/listings?sort=nearest")]
    [InlineData("/api/listings?sort=bogus")]
    [InlineData("/api/listings?ageRanges=abc")]
    [InlineData("/api/listings?conditions=Excellent")]
    [InlineData("/api/listings?ageRanges=0-1&ageRanges=2-3&ageRanges=4-5&ageRanges=6-7&ageRanges=8-9&ageRanges=10-11")]
    [InlineData("/api/listings/map-pins?conditions=bogus")]
    [InlineData("/api/listings/facets?ageRanges=9-1")]
    public async Task Bad_Lists_Return_400(string url)
    {
        var response = await _factory.CreateClient().GetAsync(url);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Repeated_Keys_Bind_And_Facets_Are_Anonymous()
    {
        var ownerId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var listingId = Guid.NewGuid();
        await _factory.SeedAsync(
            TestData.User(ownerId, $"{ownerId:N}@browse-v2.local"),
            TestData.Category(categoryId));
        var listing = TestData.Listing(listingId, ownerId, categoryId, ListingStatus.Approved);
        listing.Condition = "Excellent";
        listing.AgeFromMonths = 12;
        listing.AgeToMonths = 24;
        listing.DistrictId = TestData.KentronDistrictId;
        await _factory.SeedAsync(listing);

        var client = _factory.CreateClient();
        var query = $"categoryIds={categoryId}&categoryIds={Guid.NewGuid()}&ageRanges=0-6&ageRanges=18-&conditions=likenew&conditions=Fair";

        var list = await client.GetAsync($"/api/listings?{query}&sort=price_asc");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using var listDoc = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        var item = Assert.Single(listDoc.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(listingId, item.GetProperty("id").GetGuid());
        Assert.Equal(TestData.KentronDistrictId, item.GetProperty("district").GetProperty("id").GetGuid());

        var facets = await client.GetAsync($"/api/listings/facets?{query}");
        Assert.Equal(HttpStatusCode.OK, facets.StatusCode);
        using var facetDoc = JsonDocument.Parse(await facets.Content.ReadAsStringAsync());
        var root = facetDoc.RootElement;
        Assert.Equal(1, root.GetProperty("categories").EnumerateArray().Single(c => c.GetProperty("categoryId").GetGuid() == categoryId).GetProperty("count").GetInt32());
        Assert.True(root.TryGetProperty("districts", out _));
        Assert.True(root.TryGetProperty("minPricePerDay", out _));
        Assert.True(root.TryGetProperty("maxPricePerDay", out _));
    }
}
