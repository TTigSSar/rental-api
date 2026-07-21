using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Infrastructure.Persistence;

namespace RentalPlatform.Infrastructure.DependencyInjection.LocationBackfill;

/// <summary>
/// One-off, idempotent backfill (P1-4) for listings that predate the privacy-coordinate/district
/// features (P1-2/P1-3/P1-4): fills <see cref="Domain.Entities.Listing.PublicLatitude"/>/
/// <see cref="Domain.Entities.Listing.PublicLongitude"/> (geohash-6 cell centroid) and
/// <see cref="Domain.Entities.Listing.DistrictId"/> (point-in-polygon against the Yerevan
/// district boundaries) for any row that has an exact Latitude/Longitude but is still missing one
/// of those derived values. Structural sibling of
/// <see cref="RentalPlatform.Infrastructure.DependencyInjection.DemoContentBootstrap.DemoContentBootstrapRunner"/>
/// and <see cref="RentalPlatform.Infrastructure.DependencyInjection.DevelopmentSeed.DevelopmentSeedRunner"/>:
/// runs on every startup, in every environment, and is always safe to run again.
///
/// This is also the self-heal path (see knowledge/mistakes.md M-012) for rows a create/update
/// wrote before this feature existed, AND for rows a seed runner inserts with exact coordinates
/// but no derived values of its own — the seed runners intentionally do not duplicate the
/// snapping/lookup logic; this runner fills the gap for them on the same startup, right after
/// seeding.
///
/// Only ever FILLS a null. Never recomputes or overwrites a value that is already set — whether
/// that value was produced by an earlier run of this same runner, a normal create/update, or an
/// owner's explicit district override. A listing whose exact point legitimately falls outside all
/// 12 known districts keeps DistrictId null forever; this runner re-examines it on every run
/// (harmless — the same null comes back) rather than trying to remember "already checked".
/// </summary>
internal sealed class ListingLocationBackfillRunner
{
    private readonly AppDbContext _dbContext;
    private readonly IGeohashSnapper _geohashSnapper;
    private readonly IDistrictBoundaryProvider _districtBoundaryProvider;
    private readonly ILogger<ListingLocationBackfillRunner> _logger;

    public ListingLocationBackfillRunner(
        AppDbContext dbContext,
        IGeohashSnapper geohashSnapper,
        IDistrictBoundaryProvider districtBoundaryProvider,
        ILogger<ListingLocationBackfillRunner> logger)
    {
        _dbContext = dbContext;
        _geohashSnapper = geohashSnapper;
        _districtBoundaryProvider = districtBoundaryProvider;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        var candidates = await _dbContext.Listings
            .Where(listing => listing.Latitude != null && listing.Longitude != null &&
                (listing.PublicLatitude == null || listing.PublicLongitude == null || listing.DistrictId == null))
            .ToListAsync(cancellationToken);

        if (candidates.Count == 0)
        {
            return;
        }

        var districtIdsByCode = await _dbContext.Districts
            .ToDictionaryAsync(district => district.Code, district => district.Id, StringComparer.OrdinalIgnoreCase, cancellationToken);

        var publicCoordinatesFilled = 0;
        var districtsAssigned = 0;

        foreach (var listing in candidates)
        {
            // Both are non-null by the query filter above.
            var latitude = listing.Latitude!.Value;
            var longitude = listing.Longitude!.Value;

            if (listing.PublicLatitude is null || listing.PublicLongitude is null)
            {
                var (publicLatitude, publicLongitude) = _geohashSnapper.SnapToCellCenter(latitude, longitude);
                listing.PublicLatitude = publicLatitude;
                listing.PublicLongitude = publicLongitude;
                publicCoordinatesFilled++;
            }

            if (listing.DistrictId is null)
            {
                var code = _districtBoundaryProvider.FindDistrictCode((double)latitude, (double)longitude);
                if (code is not null && districtIdsByCode.TryGetValue(code, out var districtId))
                {
                    listing.DistrictId = districtId;
                    districtsAssigned++;
                }
            }
        }

        if (publicCoordinatesFilled == 0 && districtsAssigned == 0)
        {
            _logger.LogInformation(
                "Listing location backfill: {Count} candidate(s) examined, nothing to change.",
                candidates.Count);
            return;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Listing location backfill completed. Public coordinates filled: {PublicFilled}, districts assigned: {DistrictsAssigned} (of {Count} candidate(s) examined).",
            publicCoordinatesFilled, districtsAssigned, candidates.Count);
    }
}
