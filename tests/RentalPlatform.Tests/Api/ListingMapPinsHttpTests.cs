using System.Globalization;
using System.Net;
using System.Text.Json;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Api;

/// <summary>
/// HTTP-layer regression coverage for <c>GET /api/listings/map-pins</c> (Maps P2-1/P2-2).
///
/// Why this is needed alongside <c>ListingMapPinsQueryServiceTests</c> (service-level,
/// SQLite): that suite calls <c>ListingsQueryService.GetMapPinsAsync</c> directly with a
/// hand-constructed <c>ListingsQueryFilter</c> C# object — it never goes through ASP.NET's
/// query-string model binding, <c>[ApiController]</c>'s automatic 400 on
/// <see cref="RentalPlatform.Application.DTOs.ListingsQueryFilter.Validate"/> failure, or
/// real JSON serialization of the response DTO. Three properties the frontend now
/// concretely depends on (<c>ListingsApiService.getMapPins</c>,
/// <c>Rental-Ui/src/app/features/listings/models/listing-map-pin.model.ts</c>) have zero
/// coverage without going through the real controller:
///   1. the wire shape/casing of <c>ListingMapPinResponse</c> — it gained
///      <c>rating</c>/<c>reviewCount</c> recently, and a service-level test asserting a C#
///      property equals a value proves nothing about what actually serializes over HTTP;
///   2. the "all-four-or-none" bounding-box contract
///      (<c>minLat</c>/<c>maxLat</c>/<c>minLng</c>/<c>maxLng</c>) as bound from real query
///      string values (`decimal?` parsing), not as four fields already set on a filter object;
///   3. the <c>minLng &gt; maxLng</c> antimeridian rejection, which is a
///      <c>[ApiController]</c>-driven automatic 400 that only fires through the real HTTP
///      pipeline's model validation — the service method itself never validates anything.
/// This is exactly the M-017 class the privacy real-stack spec
/// (<c>Rental-Ui/e2e/real/map-pins-privacy.spec.ts</c>) already calls out for the SAME
/// endpoint: a unit test proving the sub-component is safe/correct is not the same claim as
/// "the live, deployed route behaves correctly" for the parts a unit test cannot reach.
/// </summary>
[Collection("Integration")]
public sealed class ListingMapPinsHttpTests
{
    private readonly RentalPlatformWebAppFactory _factory;

    public ListingMapPinsHttpTests(RentalPlatformWebAppFactory factory) => _factory = factory;

    private async Task<Guid> SeedApprovedListingAsync(decimal publicLat, decimal publicLng)
    {
        var ownerId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var listingId = Guid.NewGuid();

        await _factory.SeedAsync(
            TestData.User(ownerId, $"{ownerId:N}@map-pins-owner.local"),
            TestData.Category(categoryId));

        var listing = TestData.Listing(listingId, ownerId, categoryId, ListingStatus.Approved);
        listing.PublicLatitude = publicLat;
        listing.PublicLongitude = publicLng;
        await _factory.SeedAsync(listing);

        return listingId;
    }

    private static string Inv(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    [Fact]
    public async Task GetMapPins_Returns_200_Anonymously_With_The_Full_Pin_Shape_The_Frontend_Depends_On()
    {
        var listingId = await SeedApprovedListingAsync(40.19m, 44.52m);
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/listings/map-pins");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        Assert.False(root.GetProperty("isTruncated").GetBoolean());
        var items = root.GetProperty("items").EnumerateArray().ToList();
        var pin = Assert.Single(items, p => p.GetProperty("id").GetGuid() == listingId);

        // The exact wire shape `ListingMapPin` (Rental-Ui) binds to — camelCase field
        // names and the two fields (`rating`/`reviewCount`) that were added alongside
        // the frontend consumer this endpoint now actually has.
        Assert.Equal(40.19, pin.GetProperty("latitude").GetDouble(), precision: 3);
        Assert.Equal(44.52, pin.GetProperty("longitude").GetDouble(), precision: 3);
        Assert.False(string.IsNullOrWhiteSpace(pin.GetProperty("title").GetString()));
        Assert.True(pin.GetProperty("pricePerDay").GetDecimal() > 0);
        Assert.False(string.IsNullOrWhiteSpace(pin.GetProperty("priceUnit").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(pin.GetProperty("currency").GetString()));
        Assert.Equal(JsonValueKind.Null, pin.GetProperty("primaryImageUrl").ValueKind);
        // No reviews seeded — same null/zero contract as the service-level test,
        // now confirmed to actually reach the JSON response.
        Assert.Equal(JsonValueKind.Null, pin.GetProperty("rating").ValueKind);
        Assert.Equal(0, pin.GetProperty("reviewCount").GetInt32());
    }

    [Fact]
    public async Task GetMapPins_Ignores_Bounding_Box_Unless_All_Four_Corners_Are_Present_On_The_Real_Query_String()
    {
        // One listing well inside the box below, one far outside it.
        var insideId = await SeedApprovedListingAsync(40.19m, 44.52m);
        var outsideId = await SeedApprovedListingAsync(10.00m, 10.00m);
        var client = _factory.CreateClient();

        // Only 2 of the 4 corner params on the real query string — the backend's
        // "apply the box only when all four are HasValue" check must see this as
        // "no box" and return everything, not throw and not partially filter.
        var partial = await client.GetAsync(
            $"/api/listings/map-pins?minLat={Inv(40.0m)}&maxLat={Inv(40.3m)}");
        Assert.Equal(HttpStatusCode.OK, partial.StatusCode);
        var partialIds = await ExtractIdsAsync(partial);
        Assert.Contains(insideId, partialIds);
        Assert.Contains(outsideId, partialIds);

        // All four present and drawn tightly around only the "inside" listing —
        // the box must now actually apply through real query-string binding.
        var complete = await client.GetAsync(
            $"/api/listings/map-pins?minLat={Inv(40.1m)}&maxLat={Inv(40.3m)}&minLng={Inv(44.4m)}&maxLng={Inv(44.6m)}");
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        var completeIds = await ExtractIdsAsync(complete);
        Assert.Contains(insideId, completeIds);
        Assert.DoesNotContain(outsideId, completeIds);
    }

    [Fact]
    public async Task GetMapPins_Returns_400_When_MinLng_Greater_Than_MaxLng()
    {
        var client = _factory.CreateClient();

        // Antimeridian-crossing viewport — rejected by
        // ListingsQueryFilter.Validate, surfaced as an automatic 400 by
        // [ApiController]'s model-validation pipeline. Never exercised by the
        // service-level tests, which construct the filter directly and never
        // pass through that pipeline at all.
        var response = await client.GetAsync(
            $"/api/listings/map-pins?minLat={Inv(40.0m)}&maxLat={Inv(40.3m)}&minLng={Inv(44.6m)}&maxLng={Inv(44.4m)}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var errors = doc.RootElement.GetProperty("errors");
        Assert.True(errors.TryGetProperty("MinLng", out _) || errors.TryGetProperty("MaxLng", out _));
    }

    private static async Task<List<Guid>> ExtractIdsAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("items")
            .EnumerateArray()
            .Select(p => p.GetProperty("id").GetGuid())
            .ToList();
    }
}
