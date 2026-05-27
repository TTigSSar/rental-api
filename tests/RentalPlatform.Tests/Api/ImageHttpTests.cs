using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Api;

// Exercises the listing-image HTTP layer: upload validation, delete, and auth.
[Collection("Integration")]
public sealed class ImageHttpTests
{
    private readonly RentalPlatformWebAppFactory _factory;

    public ImageHttpTests(RentalPlatformWebAppFactory factory) => _factory = factory;

    private async Task<(Guid OwnerId, Guid ListingId)> SeedBaselineAsync()
    {
        var ownerId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var listingId = Guid.NewGuid();

        await _factory.SeedAsync(
            TestData.User(ownerId, $"{ownerId:N}@image-owner.local"),
            TestData.Category(categoryId));

        await _factory.SeedAsync(
            TestData.Listing(listingId, ownerId, categoryId, ListingStatus.Approved));

        return (ownerId, listingId);
    }

    private static MultipartFormDataContent BuildImageForm(byte[] content, string fileName, string contentType)
    {
        var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(content);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        form.Add(fileContent, "files", fileName);
        return form;
    }

    [Fact]
    public async Task Upload_Valid_Png_Returns_200_With_Image_Url()
    {
        var (ownerId, listingId) = await SeedBaselineAsync();

        var token = TestJwtTokenHelper.GenerateToken(ownerId, $"{ownerId:N}@image-owner.local");
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var form = BuildImageForm(TestData.PngBytes(), "photo.png", "image/png");
        var response = await client.PostAsync($"/api/listings/{listingId}/images", form);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var images = doc.RootElement.EnumerateArray().ToList();
        Assert.Single(images);
        Assert.True(images[0].GetProperty("isPrimary").GetBoolean());
        Assert.NotEmpty(images[0].GetProperty("url").GetString()!);

        // File storage double must have recorded exactly one saved URL.
        Assert.Single(_factory.FakeStorage.SavedUrls);
    }

    [Fact]
    public async Task Upload_Invalid_Extension_Returns_400_With_Error_Code()
    {
        var (ownerId, listingId) = await SeedBaselineAsync();

        var token = TestJwtTokenHelper.GenerateToken(ownerId, $"{ownerId:N}@image-owner.local");
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var form = BuildImageForm(TestData.PngBytes(), "malware.txt", "text/plain");
        var response = await client.PostAsync($"/api/listings/{listingId}/images", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("listing.image_invalid_type", doc.RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public async Task Upload_Non_Image_Bytes_Returns_400()
    {
        var (ownerId, listingId) = await SeedBaselineAsync();

        var token = TestJwtTokenHelper.GenerateToken(ownerId, $"{ownerId:N}@image-owner.local");
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Valid extension and content-type but bytes are not a real image (no magic signature).
        using var form = BuildImageForm(TestData.NonImageBytes(), "photo.png", "image/png");
        var response = await client.PostAsync($"/api/listings/{listingId}/images", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("listing.image_invalid_type", doc.RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public async Task Delete_Existing_Image_Returns_200_With_Remaining_Images()
    {
        var (ownerId, listingId) = await SeedBaselineAsync();
        var primaryId = Guid.NewGuid();
        var secondaryId = Guid.NewGuid();

        await _factory.SeedAsync(
            TestData.Image(primaryId, listingId, isPrimary: true, sortOrder: 0),
            TestData.Image(secondaryId, listingId, isPrimary: false, sortOrder: 1));

        var token = TestJwtTokenHelper.GenerateToken(ownerId, $"{ownerId:N}@image-owner.local");
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.DeleteAsync($"/api/listings/{listingId}/images/{secondaryId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var remaining = doc.RootElement.EnumerateArray().ToList();
        Assert.Single(remaining);
        Assert.Equal(primaryId.ToString(), remaining[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task Delete_Nonexistent_Image_Returns_404()
    {
        var (ownerId, listingId) = await SeedBaselineAsync();
        var missingId = Guid.NewGuid();

        var token = TestJwtTokenHelper.GenerateToken(ownerId, $"{ownerId:N}@image-owner.local");
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.DeleteAsync($"/api/listings/{listingId}/images/{missingId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Upload_By_Non_Owner_Returns_403()
    {
        var (_, listingId) = await SeedBaselineAsync();

        // A different user who does not own the listing.
        var intruderId = Guid.NewGuid();
        await _factory.SeedAsync(TestData.User(intruderId, $"{intruderId:N}@intruder.local"));

        var token = TestJwtTokenHelper.GenerateToken(intruderId, $"{intruderId:N}@intruder.local");
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var form = BuildImageForm(TestData.PngBytes(), "photo.png", "image/png");
        var response = await client.PostAsync($"/api/listings/{listingId}/images", form);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Upload_Then_GetMine_Returns_Listing_With_Image_Url()
    {
        var (ownerId, listingId) = await SeedBaselineAsync();

        var token = TestJwtTokenHelper.GenerateToken(ownerId, $"{ownerId:N}@image-owner.local");
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var form = BuildImageForm(TestData.PngBytes(), "photo.png", "image/png");
        await client.PostAsync($"/api/listings/{listingId}/images", form);

        var mineResponse = await client.GetAsync("/api/listings/mine");
        Assert.Equal(HttpStatusCode.OK, mineResponse.StatusCode);

        var body = await mineResponse.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var listing = doc.RootElement.EnumerateArray()
            .FirstOrDefault(l => l.GetProperty("id").GetString() == listingId.ToString());

        Assert.NotEqual(default, listing);
        var imageUrl = listing.GetProperty("primaryImageUrl").GetString();
        Assert.False(string.IsNullOrEmpty(imageUrl), "primaryImageUrl should be populated after upload");
    }

    [Fact]
    public async Task Owner_Gets_Detail_Of_Own_PendingApproval_Listing()
    {
        var ownerId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var listingId = Guid.NewGuid();

        await _factory.SeedAsync(
            TestData.User(ownerId, $"{ownerId:N}@owner-pending.local"),
            TestData.Category(categoryId));

        await _factory.SeedAsync(
            TestData.Listing(listingId, ownerId, categoryId, ListingStatus.PendingApproval));

        var token = TestJwtTokenHelper.GenerateToken(ownerId, $"{ownerId:N}@owner-pending.local");
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync($"/api/listings/{listingId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(listingId.ToString(), doc.RootElement.GetProperty("id").GetString());
    }
}
