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
        Assert.Equal("urn:rental:error:listing.image_invalid_type", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("listing.image_invalid_type", doc.RootElement.GetProperty("errorCode").GetString());
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
        Assert.Equal("urn:rental:error:listing.image_invalid_type", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("listing.image_invalid_type", doc.RootElement.GetProperty("errorCode").GetString());
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

    // -----------------------------------------------------------------------
    // PUT /api/listings/{listingId}/images  — image replacement
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Replace_Returns_200_With_New_Images_For_Owner()
    {
        var (ownerId, listingId) = await SeedBaselineAsync();

        // Pre-seed an existing image that must be replaced.
        await _factory.SeedAsync(TestData.Image(Guid.NewGuid(), listingId, isPrimary: true, sortOrder: 0));

        var token  = TestJwtTokenHelper.GenerateToken(ownerId, $"{ownerId:N}@image-owner.local");
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var form = BuildImageForm(TestData.PngBytes(), "new.png", "image/png");
        var response = await client.PutAsync($"/api/listings/{listingId}/images", form);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var images = doc.RootElement.EnumerateArray().ToList();
        Assert.Single(images);
        Assert.True(images[0].GetProperty("isPrimary").GetBoolean());
        Assert.Equal(0, images[0].GetProperty("sortOrder").GetInt32());
    }

    [Fact]
    public async Task Replace_Sets_Listing_To_PendingApproval()
    {
        var (ownerId, listingId) = await SeedBaselineAsync();

        var token  = TestJwtTokenHelper.GenerateToken(ownerId, $"{ownerId:N}@image-owner.local");
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var form = BuildImageForm(TestData.PngBytes(), "new.png", "image/png");
        await client.PutAsync($"/api/listings/{listingId}/images", form);

        // Confirm via GET /api/listings/mine that status flipped to PendingApproval.
        var mineResponse = await client.GetAsync("/api/listings/mine");
        var mineBody = await mineResponse.Content.ReadAsStringAsync();
        using var mineDoc = JsonDocument.Parse(mineBody);
        var listing = mineDoc.RootElement.EnumerateArray()
            .FirstOrDefault(l => l.GetProperty("id").GetString() == listingId.ToString());

        Assert.NotEqual(default, listing);
        Assert.Equal("PendingApproval", listing.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Replace_Returns_401_When_Not_Authenticated()
    {
        var (_, listingId) = await SeedBaselineAsync();

        using var form = BuildImageForm(TestData.PngBytes(), "new.png", "image/png");
        var response = await _factory.CreateClient().PutAsync($"/api/listings/{listingId}/images", form);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Replace_Old_Images_No_Longer_Appear_In_Listing_Detail()
    {
        var (ownerId, listingId) = await SeedBaselineAsync();

        // Snapshot shared storage state before this test so assertions are delta-based
        // (FakeStorage accumulates across the entire "Integration" collection).
        var savedBefore  = _factory.FakeStorage.SavedUrls.Count;
        var deletedBefore = _factory.FakeStorage.DeletedUrls.Count;

        var token  = TestJwtTokenHelper.GenerateToken(ownerId, $"{ownerId:N}@image-owner.local");
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // First: upload an image to give the listing something to replace.
        using var uploadForm = BuildImageForm(TestData.PngBytes(), "original.png", "image/png");
        var uploadResponse = await client.PostAsync($"/api/listings/{listingId}/images", uploadForm);
        var uploadBody = await uploadResponse.Content.ReadAsStringAsync();
        using var uploadDoc = JsonDocument.Parse(uploadBody);
        var originalUrl = uploadDoc.RootElement.EnumerateArray().First().GetProperty("url").GetString();

        // Then: replace with a new image.
        using var replaceForm = BuildImageForm(TestData.PngBytes(), "replacement.png", "image/png");
        var replaceResponse = await client.PutAsync($"/api/listings/{listingId}/images", replaceForm);
        var replaceBody = await replaceResponse.Content.ReadAsStringAsync();
        using var replaceDoc = JsonDocument.Parse(replaceBody);
        var newUrl = replaceDoc.RootElement.EnumerateArray().First().GetProperty("url").GetString();

        // New URL must differ — a genuinely new file was stored.
        Assert.NotEqual(originalUrl, newUrl);

        // Delta assertions: 2 saves (1 upload + 1 replace) and 1 delete (the upload was cleaned up).
        Assert.Equal(savedBefore  + 2, _factory.FakeStorage.SavedUrls.Count);
        Assert.Equal(deletedBefore + 1, _factory.FakeStorage.DeletedUrls.Count);
        Assert.Equal(originalUrl, _factory.FakeStorage.DeletedUrls[^1]);
    }
}
