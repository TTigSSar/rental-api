using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Infrastructure.DependencyInjection.LocationBackfill;
using RentalPlatform.Infrastructure.Persistence;

namespace RentalPlatform.Infrastructure.DependencyInjection;

public static class ListingLocationBackfillExtensions
{
    // Fills PublicLatitude/PublicLongitude/DistrictId for any listing that has exact coordinates
    // but is missing one of those derived values. Unconditional and idempotent — call on every
    // startup, in every environment, after any seeding/bootstrap step that might have just
    // inserted listings of its own (see ListingLocationBackfillRunner).
    public static async Task BackfillListingLocationsAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var geohashSnapper = scope.ServiceProvider.GetRequiredService<IGeohashSnapper>();
        var districtBoundaryProvider = scope.ServiceProvider.GetRequiredService<IDistrictBoundaryProvider>();
        var logger = scope.ServiceProvider
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger<ListingLocationBackfillRunner>();

        var runner = new ListingLocationBackfillRunner(dbContext, geohashSnapper, districtBoundaryProvider, logger);
        await runner.RunAsync(cancellationToken);
    }
}
