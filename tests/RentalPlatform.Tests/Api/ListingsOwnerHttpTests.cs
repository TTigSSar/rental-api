using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Api;

// HTTP-level regression pin for the stale-client delivery-options bug (see
// DeliveryOptionsMapper.ShouldApplyLegacyOnlyUpdate and ListingsOwnerServiceTests).
//
// ListingsOwnerServiceTests already covers ShouldApplyLegacyOnlyUpdate against
// UpdateListingRequest objects built directly in C#. That never exercises System.Text.Json
// deserialization of the PATCH body, and the API registers a JsonStringEnumConverter
// (ServiceCollectionExtensions.AddApiServices) — a real, separate pipeline the stale UI's
// exact wire payload goes through. This test pins that a PATCH body carrying ONLY the legacy
// "deliveryType" string field (the omitted-key shape a cached pre-deploy client actually sends,
// not a C#-constructed DTO) still round-trips through real JSON model binding into a no-op that
// preserves a richer DeliveryOptions flag set.
[Collection("Integration")]
public sealed class ListingsOwnerHttpTests
{
    private readonly RentalPlatformWebAppFactory _factory;

    public ListingsOwnerHttpTests(RentalPlatformWebAppFactory factory) => _factory = factory;

    private HttpClient ClientFor(Guid userId, string email)
    {
        var token = TestJwtTokenHelper.GenerateToken(userId, email);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task Patch_With_LegacyOnly_DeliveryType_Json_Body_Preserves_Multi_Select_DeliveryOptions()
    {
        var ownerId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var listingId = Guid.NewGuid();
        var email = $"{ownerId:N}@listing-owner.local";

        await _factory.SeedAsync(TestData.User(ownerId, email), TestData.Category(categoryId));
        var listing = TestData.Listing(listingId, ownerId, categoryId, ListingStatus.Approved);
        listing.DeliveryOptions = DeliveryOptions.Pickup | DeliveryOptions.Courier;
        listing.DeliveryType = DeliveryType.Pickup;
        await _factory.SeedAsync(listing);

        var client = ClientFor(ownerId, email);

        // Raw wire shape of the stale pre-deploy edit form: only the legacy scalar field,
        // "deliveryTypes" key entirely absent from the JSON body — not merely null in a C# object.
        var patchResponse = await client.PatchAsJsonAsync(
            $"/api/listings/{listingId}",
            new { deliveryType = "Pickup" });

        Assert.Equal(HttpStatusCode.NoContent, patchResponse.StatusCode);

        var mineResponse = await client.GetAsync("/api/listings/mine");
        Assert.Equal(HttpStatusCode.OK, mineResponse.StatusCode);

        using var doc = JsonDocument.Parse(await mineResponse.Content.ReadAsStringAsync());
        var updated = doc.RootElement.EnumerateArray()
            .Single(e => e.GetProperty("id").GetGuid() == listingId);

        var deliveryTypes = updated.GetProperty("deliveryTypes").EnumerateArray()
            .Select(e => e.GetString())
            .ToArray();
        Assert.Equal(new[] { "Pickup", "Courier" }, deliveryTypes);
        Assert.Equal("Pickup", updated.GetProperty("deliveryType").GetString());
    }
}
