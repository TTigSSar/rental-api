using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.Common;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Infrastructure.Persistence;

namespace RentalPlatform.Infrastructure.DependencyInjection.LocationBackfill;

/// <summary>
/// Startup reconciliation for everything derived from a coordinate. Idempotent, unconditional, runs
/// in every environment on every boot, always after any seeding/bootstrap step.
///
/// <para>Three passes, in this order, because each feeds the next:</para>
/// <list type="number">
/// <item>
/// <b>Users.</b> Fill <c>HomePublicLatitude</c>/<c>HomePublicLongitude</c> (geohash cell centroid at
/// whatever <see cref="RentalPlatform.Infrastructure.Services.GeohashSnapper.Precision"/> the
/// running code uses) and <c>HomeDistrictId</c> (point-in-polygon) for any user who has an exact
/// home point but is still missing a derived value. This is how the AddUserHomePoint migration gets
/// away with writing only the exact pair: it must never compute a geohash in SQL, because that
/// would be a second place deciding how coarse the public pair is (see IGeohashSnapper).
/// </item>
/// <item>
/// <b>Listings vs. their owner's home point.</b> A listing's location is a denormalised COPY of the
/// owner's home point, and a copy can drift: a listing created in the same instant the home point
/// moved, a row written by an older build, a home point filled in by pass 1 after the migration
/// collapsed the listings. This pass re-points any listing whose location disagrees with its
/// owner's home point. It is the self-heal path M-012 demands for derived state — the writer
/// (HomePointService) fires on an event, and an event missed by the build that was running at the
/// time never fires again.
/// </item>
/// <item>
/// <b>Listings with no owner home point.</b> The original P1-4 pass, unchanged: fill a null
/// PublicLatitude/PublicLongitude/DistrictId from the listing's own exact coordinates. Still needed
/// for rows whose owner has no home point at all (a legacy owner who never set one), which pass 2
/// deliberately leaves alone rather than wiping their location.
/// </item>
/// </list>
///
/// <para>
/// Passes 1 and 3 only ever FILL a null; pass 2 is the one place that overwrites, and only with the
/// owner's home point, which is by definition the correct value. A point that legitimately falls
/// outside all 12 known districts keeps a null district forever — re-examined on every run
/// (harmless: the same null comes back) rather than remembered as "already checked".
/// </para>
///
/// <para>
/// City is deliberately NOT reconciled here. It is chosen at create time from a fallback chain
/// (ListingsOwnerService.ResolveCity) that this runner cannot reproduce, and re-deriving it would
/// fight that chain on every boot.
/// </para>
///
/// <para>
/// Structural sibling of
/// <see cref="RentalPlatform.Infrastructure.DependencyInjection.DemoContentBootstrap.DemoContentBootstrapRunner"/>
/// and <see cref="RentalPlatform.Infrastructure.DependencyInjection.DevelopmentSeed.DevelopmentSeedRunner"/>.
/// </para>
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
        var districtIdsByCode = await _dbContext.Districts
            .ToDictionaryAsync(district => district.Code, district => district.Id, StringComparer.OrdinalIgnoreCase, cancellationToken);

        var homePointsDerived = await FillUserHomePointDerivationsAsync(districtIdsByCode, cancellationToken);

        // Committed before pass 2 runs, deliberately: pass 2 finds drifted listings with a database
        // query comparing them against their owner's home point, so the values pass 1 just derived
        // have to be IN the database, not merely tracked in memory. Without this, the boot right
        // after the AddUserHomePoint migration would compare every listing against a home point
        // whose derived half was still NULL.
        if (homePointsDerived > 0)
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        var listingsResynced = await ResyncListingsWithOwnerHomePointAsync(cancellationToken);
        var (publicCoordinatesFilled, districtsAssigned, candidatesExamined) =
            await FillMissingListingDerivationsAsync(districtIdsByCode, cancellationToken);

        var changed = homePointsDerived + listingsResynced + publicCoordinatesFilled + districtsAssigned;
        if (changed == 0)
        {
            _logger.LogInformation(
                "Listing location backfill: {Count} listing candidate(s) examined, nothing to change.",
                candidatesExamined);
            return;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        // Worded so a deploy operator can act on it. The previous phrasing reported pass 3's
        // candidate count alongside pass 3's fills — "public coordinates filled: 0 ... (of 89
        // listing candidate(s) examined)" — on exactly the boot where pass 2 had just given all 89
        // of those listings their public pair and district. Read literally that line says the
        // backfill found 89 broken listings and fixed none of them, which is the opposite of what
        // happened; see FillMissingListingDerivationsAsync for why the count was inflated. Pass 2's
        // effect is now stated explicitly, and pass 3's numerator and denominator both describe
        // pass 3, so no reading of this line is a false alarm.
        _logger.LogInformation(
            "Listing location backfill completed. Home points derived: {HomePoints}; listings re-pointed to their owner's home point, public coordinates and district included: {Resynced}; of the {Count} remaining listing(s) still missing a derived value, public coordinates filled: {PublicFilled}, districts assigned: {DistrictsAssigned}.",
            homePointsDerived, listingsResynced, candidatesExamined, publicCoordinatesFilled, districtsAssigned);
    }

    // Pass 1 — see the class remarks.
    private async Task<int> FillUserHomePointDerivationsAsync(
        IReadOnlyDictionary<string, Guid> districtIdsByCode,
        CancellationToken cancellationToken)
    {
        var users = await _dbContext.Users
            .Where(user => user.HomeLatitude != null && user.HomeLongitude != null &&
                (user.HomePublicLatitude == null || user.HomePublicLongitude == null || user.HomeDistrictId == null))
            .ToListAsync(cancellationToken);

        var derived = 0;
        foreach (var user in users)
        {
            // Both non-null by the query filter above.
            var latitude = user.HomeLatitude!.Value;
            var longitude = user.HomeLongitude!.Value;

            var result = HomePointDerivation.Derive(latitude, longitude, _geohashSnapper, _districtBoundaryProvider);
            var changed = false;

            if (user.HomePublicLatitude is null || user.HomePublicLongitude is null)
            {
                user.HomePublicLatitude = result.PublicLatitude;
                user.HomePublicLongitude = result.PublicLongitude;
                changed = true;
            }

            if (user.HomeDistrictId is null &&
                result.DistrictCode is { } code &&
                districtIdsByCode.TryGetValue(code, out var districtId))
            {
                user.HomeDistrictId = districtId;
                changed = true;
            }

            if (changed)
            {
                derived++;
            }
        }

        return derived;
    }

    // Pass 2 — see the class remarks. Only touches listings whose owner HAS a home point; an owner
    // without one keeps whatever location their listings already carry (M-038: a rule introduced
    // later must not turn legacy rows into broken ones).
    private async Task<int> ResyncListingsWithOwnerHomePointAsync(CancellationToken cancellationToken)
    {
        var drifted = await _dbContext.Listings
            .Include(listing => listing.Owner)
            .Where(listing =>
                listing.Owner.HomeLatitude != null && listing.Owner.HomeLongitude != null &&
                (listing.Latitude != listing.Owner.HomeLatitude ||
                 listing.Longitude != listing.Owner.HomeLongitude ||
                 listing.PublicLatitude != listing.Owner.HomePublicLatitude ||
                 listing.PublicLongitude != listing.Owner.HomePublicLongitude ||
                 listing.DistrictId != listing.Owner.HomeDistrictId))
            .ToListAsync(cancellationToken);

        foreach (var listing in drifted)
        {
            listing.Latitude = listing.Owner.HomeLatitude;
            listing.Longitude = listing.Owner.HomeLongitude;
            listing.PublicLatitude = listing.Owner.HomePublicLatitude;
            listing.PublicLongitude = listing.Owner.HomePublicLongitude;
            listing.DistrictId = listing.Owner.HomeDistrictId;
            listing.LocationKind = LocationKind.Home;
            // Status and UpdatedAt stay untouched — reconciling a copy is not an edit by the owner
            // and must never push an approved listing back into the moderation queue.
        }

        return drifted.Count;
    }

    // Pass 3 — the original P1-4 backfill, unchanged in behaviour.
    private async Task<(int PublicCoordinatesFilled, int DistrictsAssigned, int CandidatesExamined)>
        FillMissingListingDerivationsAsync(
            IReadOnlyDictionary<string, Guid> districtIdsByCode,
            CancellationToken cancellationToken)
    {
        // The WHERE above is evaluated by SQL Server against the DATABASE, where pass 2's re-sync is
        // still only a tracked change — it is committed by the single SaveChanges at the end of
        // RunAsync, deliberately. So on the boot right after the AddUserHomePoint migration, every
        // listing pass 2 just fixed still has NULL derived columns *in the database* and comes back
        // as a match. EF identity resolution then hands back the already-fixed tracked entity, and
        // the loop below correctly leaves it alone.
        //
        // That is right behaviour and wrong arithmetic: those rows are not candidates for this pass,
        // and counting them made the completion log read "0 filled of 89 examined" on the one boot
        // where the backfill had just done the most work it will ever do. Re-applying the predicate
        // in memory, against the entities as they actually are now, is what makes the reported count
        // describe this pass. It changes no listing either way.
        var candidates = (await _dbContext.Listings
                .Where(listing => listing.Latitude != null && listing.Longitude != null &&
                    (listing.PublicLatitude == null || listing.PublicLongitude == null || listing.DistrictId == null))
                .ToListAsync(cancellationToken))
            .Where(listing =>
                listing.Latitude is not null && listing.Longitude is not null &&
                (listing.PublicLatitude is null || listing.PublicLongitude is null || listing.DistrictId is null))
            .ToList();

        if (candidates.Count == 0)
        {
            return (0, 0, 0);
        }

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

        return (publicCoordinatesFilled, districtsAssigned, candidates.Count);
    }
}
