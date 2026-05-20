using RentalPlatform.Application.Abstractions;

namespace RentalPlatform.Tests.TestSupport;

// In-memory IFileStorageService double — records calls, touches no disk.
// Keeps the image-upload tests focused on the service's validation/ordering
// logic and fully deterministic (no temp directories, no I/O).
public sealed class FakeFileStorageService : IFileStorageService
{
    public List<string> SavedUrls { get; } = new();
    public List<string> DeletedUrls { get; } = new();

    public Task<string> SaveListingImageAsync(
        Stream content,
        string fileName,
        string contentType,
        Guid listingId,
        CancellationToken cancellationToken = default)
    {
        var extension = Path.GetExtension(fileName);
        var url = $"/uploads/listings/{listingId:N}/{Guid.NewGuid():N}{extension}";
        SavedUrls.Add(url);
        return Task.FromResult(url);
    }

    public Task<bool> DeleteListingImageAsync(string url, CancellationToken cancellationToken = default)
    {
        DeletedUrls.Add(url);
        return Task.FromResult(true);
    }
}
