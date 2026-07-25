using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    /// <remarks>
    /// Data-only, no schema change: GeohashSnapper.Precision moved from 6 (~933m x 611m cells at
    /// Yerevan's latitude) to 7 (~117m x 153m) so the new sub-kilometre radius filter has a public
    /// coordinate precise enough to make a "0.2 km" search meaningful. Every already-populated
    /// Listings.PublicLatitude/PublicLongitude was computed at the OLD precision and stays wrong
    /// (too coarse) forever unless recomputed — but recomputing needs the actual geohash
    /// bit-interleaving algorithm, which lives in one place on purpose
    /// (RentalPlatform.Infrastructure.Services.GeohashSnapper — see its doc comment: "there must
    /// never be a second place that decides how coarse the public pair is"). Reimplementing that
    /// algorithm in T-SQL here would violate exactly that rule and risk silently drifting from the
    /// C# implementation.
    ///
    /// Instead of duplicating the algorithm, this migration NULLs PublicLatitude/PublicLongitude
    /// for every listing that has an exact Latitude/Longitude, which is precisely the precondition
    /// ListingLocationBackfillRunner already watches for on every application startup (it fills
    /// PublicLatitude/PublicLongitude/DistrictId for any row with exact coordinates but a null
    /// derived value — see its doc comment). Program.cs runs ApplyMigrationsAsync() BEFORE
    /// BackfillListingLocationsAsync(), both before the app starts serving traffic, so the two
    /// steps compose into one atomic-looking startup: this migration invalidates the stale
    /// precision-6 values, and the very next line of Program.cs (already-existing code, unchanged
    /// by this migration) recomputes them at whatever precision GeohashSnapper.Precision currently
    /// is. No new runner, no new marker table, and the recompute runs at most once per already-
    /// affected row per environment (the backfill runner is a no-op once PublicLatitude/
    /// PublicLongitude are non-null again).
    ///
    /// DistrictId is deliberately left untouched — it is derived from the EXACT point via
    /// point-in-polygon, not from the fuzzed/geohash-snapped one, so it does not depend on
    /// GeohashSnapper.Precision and needs no recompute (see ListingLocationBackfillRunner and
    /// ADR-008).
    ///
    /// Listings with no exact Latitude/Longitude are excluded by the WHERE clause and are
    /// untouched — they have no PublicLatitude/PublicLongitude to invalidate in the first place.
    /// </remarks>
    public partial class InvalidatePublicCoordinatesForGeohashPrecisionUpgrade : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
UPDATE [Listings]
SET [PublicLatitude] = NULL,
    [PublicLongitude] = NULL
WHERE [Latitude] IS NOT NULL AND [Longitude] IS NOT NULL;
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Symmetric with Up, not a "restore" — there is no precision-6 snapshot to restore
            // from (PublicLatitude/PublicLongitude are a derived cache, not source data; the only
            // source of truth is Latitude/Longitude, untouched by this migration either way).
            // Re-nulling here means: if this migration is rolled back as part of rolling back the
            // whole deploy (GeohashSnapper.Precision reverting to 6 in the same rollback), the
            // next startup's ListingLocationBackfillRunner recomputes at precision 6 again, the
            // same self-healing path Up relies on.
            migrationBuilder.Sql(@"
UPDATE [Listings]
SET [PublicLatitude] = NULL,
    [PublicLongitude] = NULL
WHERE [Latitude] IS NOT NULL AND [Longitude] IS NOT NULL;
");
        }
    }
}
