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

    // Persists a chat image attachment under a conversation-scoped subdirectory and
    // returns the URL clients should use. Implementations must generate the on-disk
    // filename and never trust the user-supplied name. conversationId scopes the
    // storage namespace, mirroring how listingId scopes SaveListingImageAsync.
    Task<string> SaveChatAttachmentAsync(
        Stream content,
        string fileName,
        string contentType,
        Guid conversationId,
        CancellationToken cancellationToken = default);

    // ---- Avatars (ADR-070). Layout: uploads/avatars/{userId:N}/{guid:N}.jpg ----

    // Stores an already re-encoded JPEG under the user's avatar directory and returns its URL.
    // The file name is generated here; a partial file is removed when the write fails.
    Task<string> SaveAvatarAsync(Stream content, Guid userId, CancellationToken cancellationToken = default);

    // Deletes one avatar file. Only a URL under /uploads/avatars/{callerId:N}/ with a {guid:N}.jpg name
    // is touched; anything else (external URL, another user's path, traversal) is a no-op returning
    // false. Best effort: an IO failure is logged and reported as false, never thrown.
    Task<bool> DeleteAvatarAsync(Guid callerId, string? url, CancellationToken cancellationToken = default);

    // Removes the whole avatar directory of a user. Idempotent: nothing there counts as success.
    // False only when the wipe failed (logged as AvatarDirectoryWipeFailed, 6903).
    Task<bool> DeleteAvatarDirectoryAsync(Guid userId, CancellationToken cancellationToken = default);

    // For AvatarOrphanSweepService: directory names under the avatars root, and one user's files.
    Task<IReadOnlyList<string>> ListAvatarDirectoryNamesAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AvatarFileEntry>> ListAvatarFilesAsync(Guid userId, CancellationToken cancellationToken = default);
}

public sealed record AvatarFileEntry(string Url, DateTime LastWriteUtc);
