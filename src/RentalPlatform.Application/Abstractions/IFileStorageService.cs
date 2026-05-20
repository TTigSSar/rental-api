namespace RentalPlatform.Application.Abstractions;

public interface IFileStorageService
{
    // Persists an uploaded image under the listing-scoped subdirectory and returns
    // the URL clients should use. Implementations must generate the on-disk filename
    // and never trust the user-supplied name. listingId scopes the storage namespace.
    Task<string> SaveListingImageAsync(
        Stream content,
        string fileName,
        string contentType,
        Guid listingId,
        CancellationToken cancellationToken = default);

    // Removes the on-disk file referenced by a previously-issued listing image URL.
    // Returns true when a file was deleted. Returns false (no throw) when the URL is
    // remote, missing, or points outside the storage root — safe to call for seed
    // data that references external image hosts.
    Task<bool> DeleteListingImageAsync(string url, CancellationToken cancellationToken = default);
}
