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

    public LocalFileStorageService(IHostEnvironment hostEnvironment, IOptions<LocalFileStorageOptions> options)
    {
        var relativePath = NormalizeRelativePath(options.Value.ListingsImagesPath, hostEnvironment.ContentRootPath);
        _targetDirectory = Path.Combine(hostEnvironment.ContentRootPath, "wwwroot", relativePath);
        _publicPrefix = "/" + relativePath.Replace("\\", "/", StringComparison.Ordinal).Trim('/');
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
        if (!resolvedListingDir.StartsWith(_targetDirectory, StringComparison.OrdinalIgnoreCase))
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
        if (!candidate.StartsWith(_targetDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !candidate.Equals(_targetDirectory, StringComparison.OrdinalIgnoreCase))
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

        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("FileStorage:ListingsImagesPath must stay under wwwroot.");
        }

        return normalized;
    }
}
