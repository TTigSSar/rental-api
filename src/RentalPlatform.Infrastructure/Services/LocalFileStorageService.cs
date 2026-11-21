using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RentalPlatform.Application.Abstractions;

namespace RentalPlatform.Infrastructure.Services;

public sealed partial class LocalFileStorageService : IFileStorageService
{
    // Stable log events (ADR-070 / plan 3.3); counted with `docker logs api | grep 690x`.
    public static readonly EventId AvatarOldFileDeleteFailedEvent = new(6901, "AvatarOldFileDeleteFailed");
    public static readonly EventId AvatarDirectoryWipeFailedEvent = new(6903, "AvatarDirectoryWipeFailed");

    // The only file name this service ever writes under an avatar directory.
    [GeneratedRegex(@"^[0-9a-f]{32}\.jpg$", RegexOptions.CultureInvariant)]
    private static partial Regex AvatarFileNameRegex();

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
    private readonly string _avatarTargetDirectory;
    private readonly string _avatarPublicPrefix;
    private readonly ILogger<LocalFileStorageService>? _logger;

    public LocalFileStorageService(
        IHostEnvironment hostEnvironment,
        IOptions<LocalFileStorageOptions> options,
        ILogger<LocalFileStorageService>? logger = null)
    {
        _logger = logger;
        var avatarRelativePath = NormalizeRelativePath(options.Value.AvatarsPath, hostEnvironment.ContentRootPath);
        _avatarTargetDirectory = Path.GetFullPath(Path.Combine(hostEnvironment.ContentRootPath, "wwwroot", avatarRelativePath));
        _avatarPublicPrefix = "/" + avatarRelativePath.Replace("\\", "/", StringComparison.Ordinal).Trim('/');

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

    // ---- Avatars (ADR-070) ------------------------------------------------------------------------------

    public async Task<string> SaveAvatarAsync(Stream content, Guid userId, CancellationToken cancellationToken = default)
    {
        var userDirectory = Path.GetFullPath(Path.Combine(_avatarTargetDirectory, userId.ToString("N")));
        if (!IsWithinRoot(userDirectory, _avatarTargetDirectory))
        {
            throw new InvalidOperationException("Resolved avatar storage path escaped the configured root.");
        }

        Directory.CreateDirectory(userDirectory);

        string storageFileName;
        string fullPath;
        do
        {
            storageFileName = $"{Guid.NewGuid():N}.jpg";
            fullPath = Path.Combine(userDirectory, storageFileName);
        } while (File.Exists(fullPath));

        if (content.CanSeek)
        {
            content.Position = 0;
        }

        try
        {
            await using var destination = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await content.CopyToAsync(destination, cancellationToken);
        }
        catch
        {
            // A partial file must never be left behind; the failure itself still propagates.
            try
            {
                File.Delete(fullPath);
            }
            catch (Exception cleanup)
            {
                _logger?.LogWarning(cleanup, "Could not remove partial avatar file {Path}.", fullPath);
            }

            throw;
        }

        return $"{_avatarPublicPrefix}/{userId:N}/{storageFileName}";
    }

    public Task<bool> DeleteAvatarAsync(Guid callerId, string? url, CancellationToken cancellationToken = default)
    {
        if (!TryResolveAvatarFile(callerId, url, out var candidate))
        {
            return Task.FromResult(false);
        }

        try
        {
            if (!File.Exists(candidate))
            {
                return Task.FromResult(false);
            }

            File.Delete(candidate);
            return Task.FromResult(true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(AvatarOldFileDeleteFailedEvent, exception, "Could not delete avatar file {Url} of user {UserId}.", url, callerId);
            return Task.FromResult(false);
        }
    }

    public Task<bool> DeleteAvatarDirectoryAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var userDirectory = Path.GetFullPath(Path.Combine(_avatarTargetDirectory, userId.ToString("N")));
        if (!IsWithinRoot(userDirectory, _avatarTargetDirectory))
        {
            return Task.FromResult(false);
        }

        try
        {
            if (Directory.Exists(userDirectory))
            {
                Directory.Delete(userDirectory, recursive: true);
            }

            return Task.FromResult(true);
        }
        catch (Exception exception) when (exception is DirectoryNotFoundException or FileNotFoundException)
        {
            return Task.FromResult(true); // raced with another remover: already gone
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger?.LogError(AvatarDirectoryWipeFailedEvent, exception, "Could not wipe the avatar directory of user {UserId}.", userId);
            return Task.FromResult(false);
        }
    }

    public Task<IReadOnlyList<string>> ListAvatarDirectoryNamesAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<string> names = Directory.Exists(_avatarTargetDirectory)
            ? Directory.EnumerateDirectories(_avatarTargetDirectory).Select(path => Path.GetFileName(path)).ToList()
            : new List<string>();
        return Task.FromResult(names);
    }

    public Task<IReadOnlyList<AvatarFileEntry>> ListAvatarFilesAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var userDirectory = Path.GetFullPath(Path.Combine(_avatarTargetDirectory, userId.ToString("N")));
        IReadOnlyList<AvatarFileEntry> files = IsWithinRoot(userDirectory, _avatarTargetDirectory) && Directory.Exists(userDirectory)
            ? Directory.EnumerateFiles(userDirectory)
                .Select(path => new FileInfo(path))
                .Where(info => AvatarFileNameRegex().IsMatch(info.Name))
                .Select(info => new AvatarFileEntry($"{_avatarPublicPrefix}/{userId:N}/{info.Name}", info.LastWriteTimeUtc))
                .ToList()
            : new List<AvatarFileEntry>();
        return Task.FromResult(files);
    }

    // Only /uploads/avatars/{callerId:N}/{guid:N}.jpg of the CALLER resolves; an external URL, another
    // user's path, a traversal attempt or any odd character does not.
    private bool TryResolveAvatarFile(Guid callerId, string? url, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        var expectedPrefix = $"{_avatarPublicPrefix}/{callerId:N}/";
        if (!url.StartsWith(expectedPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var fileName = url[expectedPrefix.Length..];
        if (fileName.IndexOfAny(new[] { '/', '\\', '?', '#', '%' }) >= 0
            || fileName.Contains("..", StringComparison.Ordinal)
            || !AvatarFileNameRegex().IsMatch(fileName))
        {
            return false;
        }

        var candidate = Path.GetFullPath(Path.Combine(_avatarTargetDirectory, callerId.ToString("N"), fileName));
        if (!IsWithinRoot(candidate, _avatarTargetDirectory))
        {
            return false;
        }

        path = candidate;
        return true;
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
