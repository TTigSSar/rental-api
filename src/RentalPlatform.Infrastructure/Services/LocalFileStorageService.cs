using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using RentalPlatform.Application.Abstractions;

namespace RentalPlatform.Infrastructure.Services;

public sealed class LocalFileStorageService : IFileStorageService
{
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg",
        ".jpeg",
        ".png",
        ".webp",
        ".gif"
    };

    private readonly string _targetDirectory;
    private readonly string _publicPrefix;
    private readonly string _chatTargetDirectory;
    private readonly string _chatPublicPrefix;

    public LocalFileStorageService(IHostEnvironment hostEnvironment, IOptions<LocalFileStorageOptions> options)
    {
        var relativePath = NormalizeRelativePath(options.Value.ListingsImagesPath, hostEnvironment.ContentRootPath);
        // Path.GetFullPath is essential here, not just cosmetic: the configured relative
        // path may still contain forward slashes (e.g. "uploads/chat"), and a bare
        // Path.Combine does not normalize separators *inside* an already-combined segment.
        // On Windows that leaves _targetDirectory half-normalized ("...\wwwroot\uploads/chat"),
        // while every candidate path we compare it against has been through GetFullPath and
        // is fully backslash-normalized — so a naive StartsWith would always fail containment.
        _targetDirectory = Path.GetFullPath(Path.Combine(hostEnvironment.ContentRootPath, "wwwroot", relativePath));
        _publicPrefix = "/" + relativePath.Replace("\\", "/", StringComparison.Ordinal).Trim('/');

        var chatRelativePath = NormalizeRelativePath(options.Value.ChatAttachmentsPath, hostEnvironment.ContentRootPath);
        _chatTargetDirectory = Path.GetFullPath(Path.Combine(hostEnvironment.ContentRootPath, "wwwroot", chatRelativePath));
        _chatPublicPrefix = "/" + chatRelativePath.Replace("\\", "/", StringComparison.Ordinal).Trim('/');
    }

    public async Task<string> SaveListingImageAsync(
        Stream content,
        string fileName,
        string contentType,
        Guid listingId,
        CancellationToken cancellationToken = default)
    {
        var extension = NormalizeExtension(fileName);

        // Per-listing subdirectory keeps the storage flat-listable per listing and means a
        // listing-delete cleanup (when it exists) can drop one directory rather than scan.
        // listingId comes from server-side data, so it's safe to use as a path segment —
        // but we still verify path containment as defense in depth.
        var listingDirectory = Path.Combine(_targetDirectory, listingId.ToString("N"));
        var resolvedListingDir = Path.GetFullPath(listingDirectory);
        if (!IsWithinRoot(resolvedListingDir, _targetDirectory))
        {
            throw new InvalidOperationException("Resolved listing storage path escaped the configured root.");
        }

        Directory.CreateDirectory(resolvedListingDir);

        // GUID-N is 32 hex chars with no separators — collision probability is negligible,
        // but loop anyway so a hypothetical collision can never silently overwrite.
        string storageFileName;
        string fullPath;
        do
        {
            storageFileName = $"{Guid.NewGuid():N}{extension}";
            fullPath = Path.Combine(resolvedListingDir, storageFileName);
        } while (File.Exists(fullPath));

        if (content.CanSeek)
        {
            content.Position = 0;
        }

        // FileMode.CreateNew throws if the file appeared between the existence check above
        // and this open — eliminating the last sliver of TOCTOU risk on overwrite.
        await using var destination = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await content.CopyToAsync(destination, cancellationToken);

        return $"{_publicPrefix}/{listingId:N}/{storageFileName}";
    }

    public async Task<string> SaveChatAttachmentAsync(
        Stream content,
        string fileName,
        string contentType,
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        var extension = NormalizeExtension(fileName);

        // Per-conversation subdirectory, mirroring the per-listing subdirectory above.
        // conversationId comes from server-side data, so it's safe to use as a path
        // segment — but we still verify path containment as defense in depth.
        var conversationDirectory = Path.Combine(_chatTargetDirectory, conversationId.ToString("N"));
        var resolvedConversationDir = Path.GetFullPath(conversationDirectory);
        if (!IsWithinRoot(resolvedConversationDir, _chatTargetDirectory))
        {
            throw new InvalidOperationException("Resolved chat attachment storage path escaped the configured root.");
        }

        Directory.CreateDirectory(resolvedConversationDir);

        // GUID-N is 32 hex chars with no separators — collision probability is negligible,
        // but loop anyway so a hypothetical collision can never silently overwrite.
        string storageFileName;
        string fullPath;
        do
        {
            storageFileName = $"{Guid.NewGuid():N}{extension}";
            fullPath = Path.Combine(resolvedConversationDir, storageFileName);
        } while (File.Exists(fullPath));

        if (content.CanSeek)
        {
            content.Position = 0;
        }

        // FileMode.CreateNew throws if the file appeared between the existence check above
        // and this open — eliminating the last sliver of TOCTOU risk on overwrite.
        await using var destination = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await content.CopyToAsync(destination, cancellationToken);

        return $"{_chatPublicPrefix}/{conversationId:N}/{storageFileName}";
    }

    public Task<bool> DeleteListingImageAsync(string url, CancellationToken cancellationToken = default)
    {
        // Only operate on URLs the local storage actually issued. Seed data references
        // external picsum.photos URLs which must never trigger a disk operation.
        if (string.IsNullOrWhiteSpace(url) || !url.StartsWith(_publicPrefix + "/", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(false);
        }

        var relativePart = url[(_publicPrefix.Length + 1)..]
            .Replace('/', Path.DirectorySeparatorChar);

        var candidate = Path.GetFullPath(Path.Combine(_targetDirectory, relativePart));

        // Defense in depth — refuse anything that escapes the storage root.
        if (!IsWithinRoot(candidate, _targetDirectory))
        {
            return Task.FromResult(false);
        }

        if (!File.Exists(candidate))
        {
            return Task.FromResult(false);
        }

        File.Delete(candidate);
        return Task.FromResult(true);
    }

    private static string NormalizeExtension(string fileName)
    {
        // Path.GetFileName strips any directory components a malicious client may have included.
        var safeName = Path.GetFileName(fileName);
        var extension = Path.GetExtension(safeName).ToLowerInvariant();

        if (!AllowedExtensions.Contains(extension))
        {
            throw new InvalidOperationException("Unsupported file extension for listing image.");
        }

        return extension;
    }

    private static string NormalizeRelativePath(string configuredPath, string contentRootPath)
    {
        var normalized = configuredPath.Trim().TrimStart('/', '\\');
        var root = Path.GetFullPath(Path.Combine(contentRootPath, "wwwroot"));
        var full = Path.GetFullPath(Path.Combine(root, normalized));

        if (!IsWithinRoot(full, root))
        {
            throw new InvalidOperationException("FileStorage:ListingsImagesPath must stay under wwwroot.");
        }

        return normalized;
    }

    // Ordinal containment check used everywhere we must prove a resolved path stays inside
    // a configured root. Both arguments MUST already be fully normalized via Path.GetFullPath
    // before calling this — comparing a half-normalized path (e.g. one built with a bare
    // Path.Combine over a config value that still contains "/" on Windows) against a fully
    // normalized one silently fails containment even for legitimate paths.
    //
    // We require an exact match on the root OR a match on (root + separator) rather than a
    // plain StartsWith(root) — a plain prefix check would let a sibling directory like
    // "uploads/chat-evil" pass as "inside" "uploads/chat" purely because the strings share a
    // prefix, defeating the whole point of the containment check.
    private static bool IsWithinRoot(string candidate, string root)
    {
        return candidate.Equals(root, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
