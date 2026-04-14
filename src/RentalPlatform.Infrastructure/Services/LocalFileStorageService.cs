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
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_targetDirectory);

        var originalFileName = Path.GetFileName(fileName);
        var extension = Path.GetExtension(originalFileName);
        if (!AllowedExtensions.Contains(extension))
        {
            throw new InvalidOperationException("Unsupported file extension for listing image.");
        }

        var storageFileName = $"{Guid.NewGuid():N}{extension.ToLowerInvariant()}";
        var fullPath = Path.Combine(_targetDirectory, storageFileName);

        await using var destination = File.Create(fullPath);
        await content.CopyToAsync(destination, cancellationToken);

        return $"{_publicPrefix}/{storageFileName}";
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
