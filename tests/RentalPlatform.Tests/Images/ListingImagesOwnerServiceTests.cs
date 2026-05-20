using RentalPlatform.Application.DTOs;
using RentalPlatform.Application.Services;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Infrastructure.Persistence;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Images;

// Image upload/delete coverage. Runs the real ListingImagesOwnerService over the real
// ListingsOwnerStore against in-memory SQLite, with an in-memory file-storage double.
public sealed class ListingImagesOwnerServiceTests
{
    private static readonly Guid OwnerId = new("b0000000-0000-0000-0000-000000000001");
    private static readonly Guid OtherUserId = new("b0000000-0000-0000-0000-000000000002");
    private static readonly Guid CategoryId = new("b0000000-0000-0000-0000-000000000003");
    private static readonly Guid ListingId = new("b0000000-0000-0000-0000-000000000004");

    private static async Task SeedBaselineAsync(SqliteTestDatabase db)
    {
        await db.SeedAsync(
            TestData.User(OwnerId, "owner@test.local"),
            TestData.User(OtherUserId, "other@test.local"),
            TestData.Category(CategoryId));

        await db.SeedAsync(TestData.Listing(ListingId, OwnerId, CategoryId, ListingStatus.Approved));
    }

    private static ListingImagesOwnerService CreateService(
        AppDbContext context,
        Guid currentUserId,
        FakeFileStorageService storage) =>
        new(new FakeCurrentUserContext(currentUserId), new ListingsOwnerStore(context), storage);

    [Fact]
    public async Task Upload_Succeeds_For_Valid_Image()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);
        var storage = new FakeFileStorageService();

        await using var context = db.CreateContext();
        var service = CreateService(context, OwnerId, storage);

        var result = await service.UploadAsync(ListingId, new[] { TestData.UploadRequest() });

        Assert.True(result.IsSuccess);
        var image = Assert.Single(result.Value!);
        Assert.True(image.IsPrimary);
        Assert.Equal(0, image.SortOrder);
        Assert.Single(storage.SavedUrls);
    }

    [Fact]
    public async Task Upload_Rejects_Invalid_Extension()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);
        var storage = new FakeFileStorageService();

        await using var context = db.CreateContext();
        var service = CreateService(context, OwnerId, storage);

        var request = TestData.UploadRequest(fileName: "malware.txt");
        var result = await service.UploadAsync(ListingId, new[] { request });

        Assert.False(result.IsSuccess);
        Assert.Equal("listing.image_invalid_type", result.Error!.Code);
        Assert.Empty(storage.SavedUrls);
    }

    [Fact]
    public async Task Upload_Rejects_Mismatched_Magic_Bytes()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);
        var storage = new FakeFileStorageService();

        await using var context = db.CreateContext();
        var service = CreateService(context, OwnerId, storage);

        // Valid extension + content type, but the bytes are not a real image.
        var request = TestData.UploadRequest(content: TestData.NonImageBytes());
        var result = await service.UploadAsync(ListingId, new[] { request });

        Assert.False(result.IsSuccess);
        Assert.Equal("listing.image_invalid_type", result.Error!.Code);
        Assert.Empty(storage.SavedUrls);
    }

    [Fact]
    public async Task Upload_Rejects_File_Exceeding_Size_Limit()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);
        var storage = new FakeFileStorageService();

        await using var context = db.CreateContext();
        var service = CreateService(context, OwnerId, storage);

        // Declared length is over the 5 MB cap — rejected before any buffering.
        var request = TestData.UploadRequest(
            declaredLength: ListingImagesOwnerService.MaxBytesPerFile + 1);
        var result = await service.UploadAsync(ListingId, new[] { request });

        Assert.False(result.IsSuccess);
        Assert.Equal("listing.image_too_large", result.Error!.Code);
        Assert.Empty(storage.SavedUrls);
    }

    [Fact]
    public async Task Upload_Rejects_More_Than_Ten_Files_Per_Request()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);
        var storage = new FakeFileStorageService();

        await using var context = db.CreateContext();
        var service = CreateService(context, OwnerId, storage);

        var files = Enumerable
            .Range(0, ListingImagesOwnerService.MaxImagesPerUpload + 1)
            .Select(_ => TestData.UploadRequest())
            .ToArray();

        var result = await service.UploadAsync(ListingId, files);

        Assert.False(result.IsSuccess);
        Assert.Equal("listing.image_too_many", result.Error!.Code);
        Assert.Empty(storage.SavedUrls);
    }

    [Fact]
    public async Task Upload_Enforces_Per_Listing_Image_Limit()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);
        var storage = new FakeFileStorageService();

        // Pre-fill the listing to one below the cap.
        var existing = Enumerable
            .Range(0, ListingImagesOwnerService.MaxImagesPerListing - 1)
            .Select(index => (object)TestData.Image(Guid.NewGuid(), ListingId, index == 0, index))
            .ToArray();
        await db.SeedAsync(existing);

        await using var context = db.CreateContext();
        var service = CreateService(context, OwnerId, storage);

        // 19 existing + 2 new = 21, over the 20 cap.
        var result = await service.UploadAsync(
            ListingId,
            new[] { TestData.UploadRequest(), TestData.UploadRequest() });

        Assert.False(result.IsSuccess);
        Assert.Equal("listing.image_listing_limit", result.Error!.Code);
        Assert.Empty(storage.SavedUrls);
    }

    [Fact]
    public async Task Delete_Removes_Image_For_Owner()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);
        var primaryId = Guid.NewGuid();
        var secondaryId = Guid.NewGuid();
        await db.SeedAsync(
            TestData.Image(primaryId, ListingId, isPrimary: true, sortOrder: 0),
            TestData.Image(secondaryId, ListingId, isPrimary: false, sortOrder: 1));
        var storage = new FakeFileStorageService();

        await using var context = db.CreateContext();
        var service = CreateService(context, OwnerId, storage);

        var result = await service.DeleteAsync(ListingId, secondaryId);

        Assert.True(result.IsSuccess);
        var remaining = Assert.Single(result.Value!);
        Assert.Equal(primaryId, remaining.Id);
        Assert.True(remaining.IsPrimary);
        Assert.Single(storage.DeletedUrls);
    }

    [Fact]
    public async Task Delete_Primary_Image_Promotes_Next_Image()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);
        var primaryId = Guid.NewGuid();
        var secondaryId = Guid.NewGuid();
        await db.SeedAsync(
            TestData.Image(primaryId, ListingId, isPrimary: true, sortOrder: 0),
            TestData.Image(secondaryId, ListingId, isPrimary: false, sortOrder: 1));
        var storage = new FakeFileStorageService();

        await using var context = db.CreateContext();
        var service = CreateService(context, OwnerId, storage);

        var result = await service.DeleteAsync(ListingId, primaryId);

        Assert.True(result.IsSuccess);
        var remaining = Assert.Single(result.Value!);
        Assert.Equal(secondaryId, remaining.Id);
        Assert.True(remaining.IsPrimary);

        // Confirm the promotion was persisted, not just reflected in the response.
        await using var verifyContext = db.CreateContext();
        var persisted = await verifyContext.FindAsync<RentalPlatform.Domain.Entities.ListingImage>(secondaryId);
        Assert.NotNull(persisted);
        Assert.True(persisted!.IsPrimary);
    }

    [Fact]
    public async Task Delete_Rejected_When_Caller_Is_Not_Listing_Owner()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);
        var imageId = Guid.NewGuid();
        await db.SeedAsync(TestData.Image(imageId, ListingId, isPrimary: true, sortOrder: 0));
        var storage = new FakeFileStorageService();

        await using var context = db.CreateContext();
        var service = CreateService(context, OtherUserId, storage);

        var result = await service.DeleteAsync(ListingId, imageId);

        Assert.False(result.IsSuccess);
        Assert.Equal("listing.forbidden", result.Error!.Code);
        Assert.Empty(storage.DeletedUrls);
    }
}
