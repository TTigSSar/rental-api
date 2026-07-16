using Microsoft.Extensions.Logging;
using RentalPlatform.Application.Abstractions;

namespace RentalPlatform.Infrastructure.DependencyInjection.SeedSupport;

/// <summary>
/// Shared "download a seed image, fall back to a local SVG on failure" logic used by both the
/// Development seed (<see cref="RentalPlatform.Infrastructure.DependencyInjection.DevelopmentSeed.DevelopmentSeedRunner"/>)
/// and the Production demo-content bootstrap. A single implementation keeps the two runners from
/// drifting: both source the exact same listing/image data from
/// <see cref="RentalPlatform.Infrastructure.DependencyInjection.DevelopmentSeed.DevelopmentSeedData"/>.
/// </summary>
internal static class SeedImageResolver
{
    /// <summary>
    /// Downloads <paramref name="sourceUrl"/> (Unsplash) and persists it via <paramref name="fileStorage"/>,
    /// returning the local URL. Non-http source URLs are returned unchanged. On any failure (network,
    /// non-success status), returns <paramref name="fallbackUrl"/> when set, otherwise the original
    /// source URL — the seeded catalogue must stay usable even with no internet access.
    /// </summary>
    public static async Task<string> ResolveImageUrlAsync(
        HttpClient http,
        IFileStorageService fileStorage,
        ILogger logger,
        string sourceUrl,
        Guid listingId,
        string fallbackUrl,
        CancellationToken cancellationToken)
    {
        if (!sourceUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return sourceUrl;

        try
        {
            using var response = await http.GetAsync(sourceUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Seed image download returned {Status} for {Url}", response.StatusCode, sourceUrl);
                return string.IsNullOrEmpty(fallbackUrl) ? sourceUrl : fallbackUrl;
            }

            var contentType = response.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
            var ext = contentType switch
            {
                "image/png" => ".png",
                "image/webp" => ".webp",
                "image/gif" => ".gif",
                _ => ".jpg"
            };

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var localUrl = await fileStorage.SaveListingImageAsync(
                stream, $"seed{ext}", contentType, listingId, cancellationToken);
            logger.LogInformation("Seed image saved locally as {LocalUrl}", localUrl);
            return localUrl;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to download seed image from {Url}", sourceUrl);
            return string.IsNullOrEmpty(fallbackUrl) ? sourceUrl : fallbackUrl;
        }
    }
}
