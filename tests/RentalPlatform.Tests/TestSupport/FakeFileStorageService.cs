using RentalPlatform.Application.Abstractions;

namespace RentalPlatform.Tests.TestSupport;

// In-memory IFileStorageService double — records calls, touches no disk.
// Keeps the image-upload tests focused on the service's validation/ordering
// logic and fully deterministic (no temp directories, no I/O).
public sealed class FakeFileStorageService : IFileStorageService
{
    public List<string> SavedUrls { get; } = new();
    public List<string> DeletedUrls { get; } = new();
    public List<string> SavedChatUrls { get; } = new();

    // Avatar calls (ADR-070), recorded for the account-deletion and avatar-service tests.
    public List<string> SavedAvatarUrls { get; } = new();
    public List<(Guid CallerId, string? Url)> DeletedAvatars { get; } = new();
    public List<Guid> DeletedAvatarDirectories { get; } = new();
    public List<Guid> AvatarDirectoryWipeOrder { get; } = new();
    public bool ThrowOnDeleteAvatarDirectory { get; set; }
    public bool ThrowOnDeleteAvatar { get; set; }
    public bool ThrowOnSaveAvatar { get; set; }
    public Func<Guid, string>? AvatarUrlFactory { get; set; }
    public Func<Task>? OnSaveAvatar { get; set; }
    public List<string> AvatarDirectoryNames { get; } = new();
    public Dictionary<Guid, List<AvatarFileEntry>> AvatarFiles { get; } = new();

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

    public Task<string> SaveChatAttachmentAsync(
        Stream content,
        string fileName,
        string contentType,
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        var extension = Path.GetExtension(fileName);
        var url = $"/uploads/chat/{conversationId:N}/{Guid.NewGuid():N}{extension}";
        SavedChatUrls.Add(url);
        return Task.FromResult(url);
    }

    public async Task<string> SaveAvatarAsync(Stream content, Guid userId, CancellationToken cancellationToken = default)
    {
        if (ThrowOnSaveAvatar)
        {
            throw new IOException("disk full");
        }

        if (OnSaveAvatar is not null)
        {
            await OnSaveAvatar();
        }

        var url = AvatarUrlFactory?.Invoke(userId) ?? $"/uploads/avatars/{userId:N}/{Guid.NewGuid():N}.jpg";
        SavedAvatarUrls.Add(url);
        return url;
    }

    public Task<bool> DeleteAvatarAsync(Guid callerId, string? url, CancellationToken cancellationToken = default)
    {
        if (ThrowOnDeleteAvatar)
        {
            throw new IOException("locked");
        }

        DeletedAvatars.Add((callerId, url));
        return Task.FromResult(true);
    }

    public Task<bool> DeleteAvatarDirectoryAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        if (ThrowOnDeleteAvatarDirectory)
        {
            throw new IOException("locked");
        }

        DeletedAvatarDirectories.Add(userId);
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<string>> ListAvatarDirectoryNamesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>(AvatarDirectoryNames.ToList());

    public Task<IReadOnlyList<AvatarFileEntry>> ListAvatarFilesAsync(Guid userId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AvatarFileEntry>>(AvatarFiles.TryGetValue(userId, out var files) ? files.ToList() : new List<AvatarFileEntry>());
}
