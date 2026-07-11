using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using RentalPlatform.Infrastructure.Services;
using Xunit;

namespace RentalPlatform.Tests.Infrastructure;

// Exercises the REAL LocalFileStorageService (not FakeFileStorageService). The fake bypasses
// disk I/O entirely, so it never caught two live bugs: (1) on Windows, a bare Path.Combine
// over a config value that still contains "/" (e.g. "uploads/chat") leaves the target
// directory half-normalized, so every save 500'd with "escaped the configured root" even for
// a perfectly legitimate path; (2) a naive StartsWith(root) containment check lets a sibling
// directory like "uploads/listings-evil" masquerade as being inside "uploads/listings".
public sealed class LocalFileStorageServiceTests : IDisposable
{
    private readonly string _contentRoot;

    public LocalFileStorageServiceTests()
    {
        _contentRoot = Path.Combine(Path.GetTempPath(), "rental-storage-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_contentRoot, "wwwroot"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_contentRoot))
        {
            Directory.Delete(_contentRoot, recursive: true);
        }
    }

    private LocalFileStorageService CreateService(string listingsPath = "uploads/listings", string chatPath = "uploads/chat") =>
        new(new FakeHostEnvironment(_contentRoot), Options.Create(new LocalFileStorageOptions
        {
            ListingsImagesPath = listingsPath,
            ChatAttachmentsPath = chatPath
        }));

    private static Stream SampleImageContent() => new MemoryStream(new byte[] { 1, 2, 3, 4 });

    [Fact]
    public async Task SaveListingImageAsync_Succeeds_And_Lands_Inside_Root()
    {
        // This is the direct regression test for the Windows path-containment bug: on the
        // broken code this call threw InvalidOperationException("...escaped the configured
        // root.") on every Windows machine, because "uploads/listings" (with a literal "/")
        // was never run through Path.GetFullPath before the StartsWith comparison.
        var service = CreateService();
        var listingId = Guid.NewGuid();

        var url = await service.SaveListingImageAsync(SampleImageContent(), "photo.jpg", "image/jpeg", listingId);

        Assert.StartsWith($"/uploads/listings/{listingId:N}/", url, StringComparison.Ordinal);
        var expectedDisk = Path.Combine(_contentRoot, "wwwroot", "uploads", "listings", listingId.ToString("N"));
        var savedFile = Directory.GetFiles(expectedDisk);
        Assert.Single(savedFile);
    }

    [Fact]
    public async Task SaveChatAttachmentAsync_Succeeds_And_Lands_Inside_Root()
    {
        // Same regression as above, for the chat attachment path specifically (the code path
        // the verifier caught failing live on Windows).
        var service = CreateService();
        var conversationId = Guid.NewGuid();

        var url = await service.SaveChatAttachmentAsync(SampleImageContent(), "photo.png", "image/png", conversationId);

        Assert.StartsWith($"/uploads/chat/{conversationId:N}/", url, StringComparison.Ordinal);
        var expectedDisk = Path.Combine(_contentRoot, "wwwroot", "uploads", "chat", conversationId.ToString("N"));
        var savedFile = Directory.GetFiles(expectedDisk);
        Assert.Single(savedFile);
    }

    [Fact]
    public async Task DeleteListingImageAsync_Rejects_Traversal_Into_Sibling_Directory()
    {
        // Regression test for the sibling-directory bypass: a naive candidate.StartsWith(root)
        // (without requiring root + separator) would treat ".../uploads/listings-evil/x.png"
        // as "inside" ".../uploads/listings" purely because the strings share a prefix. A
        // relative URL that walks up one level and back down into a sibling reproduces this.
        var service = CreateService();

        // Create the sibling directory with a file in it so a successful (buggy) delete would
        // have something real to remove — proving the rejection isn't just "file not found".
        var siblingDir = Path.Combine(_contentRoot, "wwwroot", "uploads", "listings-evil");
        Directory.CreateDirectory(siblingDir);
        var siblingFile = Path.Combine(siblingDir, "secret.png");
        await File.WriteAllBytesAsync(siblingFile, new byte[] { 9 });

        var maliciousUrl = "/uploads/listings/../listings-evil/secret.png";

        var deleted = await service.DeleteListingImageAsync(maliciousUrl);

        Assert.False(deleted);
        Assert.True(File.Exists(siblingFile));
    }

    [Fact]
    public async Task DeleteListingImageAsync_Deletes_A_File_It_Actually_Issued()
    {
        var service = CreateService();
        var listingId = Guid.NewGuid();
        var url = await service.SaveListingImageAsync(SampleImageContent(), "photo.jpg", "image/jpeg", listingId);

        var deleted = await service.DeleteListingImageAsync(url);

        Assert.True(deleted);
    }

    private sealed class FakeHostEnvironment : IHostEnvironment
    {
        public FakeHostEnvironment(string contentRootPath)
        {
            ContentRootPath = contentRootPath;
        }

        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "RentalPlatform.Tests";
        public string ContentRootPath { get; set; }
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
