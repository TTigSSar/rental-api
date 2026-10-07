using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalPlatform.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Gives every user a home point and makes it the single source of their listings' location.
    /// </summary>
    /// <remarks>
    /// <para>
    /// SCHEMA: six nullable Home* columns on Users (+ FK/index to Districts) and the
    /// ListingLocationsBeforeHomePoint snapshot table.
    /// </para>
    /// <para>
    /// DATA — and this is the part that needs human eyes, because it is lossy:
    /// </para>
    /// <list type="number">
    /// <item>
    /// Every listing's current location is copied into ListingLocationsBeforeHomePoint. This is the
    /// ONLY undo tape: step 3 collapses an owner's listings onto one point, and many distinct points
    /// cannot be recovered from the one they became. Down restores from this table.
    /// </item>
    /// <item>
    /// Each owner's home point is taken from their NEWEST listing that has coordinates
    /// (ROW_NUMBER() OVER (PARTITION BY OwnerId ORDER BY CreatedAt DESC, Id DESC)). Guarded by
    /// <c>WHERE u.HomeLatitude IS NULL</c> so a home point that already exists is never overwritten
    /// and re-running is a no-op.
    /// </item>
    /// <item>
    /// Every listing of that owner is moved onto the home point — the exact coordinates AND the
    /// City/Country of the very listing step 2 took the home point from — and its PublicLatitude/
    /// PublicLongitude/DistrictId are set to NULL.
    /// </item>
    /// </list>
    /// <para>
    /// RE-APPLYING Up IS A NO-OP, and that is a property of all THREE steps, each by its own
    /// guard — it used to be true of steps 1–2 only:
    /// </para>
    /// <list type="bullet">
    /// <item>step 1 by <c>WHERE NOT EXISTS</c> on the tape's primary key;</item>
    /// <item>step 2 by <c>WHERE u.HomeLatitude IS NULL</c>;</item>
    /// <item>
    /// step 3 by skipping any row already in the state it would leave it in. Its City/Country winner
    /// is also elected over the TAPE rather than over [Listings], so the window keeps meaning "the
    /// newest listing that HAD coordinates" after this step has given them to everything — see the
    /// long comment on step 3 for both, for the one consequence worth knowing, and for why only the
    /// first of the two is observable.
    /// </item>
    /// </list>
    /// <para>
    /// This matters because re-application is not hypothetical: ADR-023 requires rehearsing every
    /// migration-bearing production deploy against a restored backup, which runs this file against
    /// real data by hand, and a retried deploy or a container restart mid-rollout does the same
    /// thing unattended.
    /// </para>
    /// <para>
    /// INVARIANT step 3 has to keep: a listing's City names the place its pin is actually in. City
    /// is an exact-match public filter (ListingsQueryService), so a listing left with its old City
    /// while pinned on the owner's home point would be invisible to ?city=&lt;home city&gt; and at
    /// the same time plainly visible on the map in that city — two surfaces disagreeing about one
    /// row. The trigger is not "an owner outside Yerevan": it is any listing whose City differs
    /// from the City of its owner's newest located listing. Step 3 therefore writes City/Country
    /// from that same listing, electing it with the same window as step 2 (same partition, same
    /// ordering) but over the step 1 TAPE instead of [Listings], so "located" still means "located
    /// before the collapse" once the collapse has located everything. No extra rows are written:
    /// step 3 already updates exactly these. Fully reversible — the tape in step 1 captures
    /// City/Country and Down restores both.
    /// ListingLocationBackfillRunner still does not reconcile City or Country, and nothing here
    /// asks it to — which is also why neither is writable through the API any more.
    /// </para>
    /// <para>
    /// NOTHING derived is computed here. The geohash public pair and the point-in-polygon district
    /// are deliberately left NULL on both Users and Listings, because reimplementing either in SQL
    /// would create a second place that decides how coarse the public pair is — which the code's own
    /// doc comments forbid (see IGeohashSnapper). ListingLocationBackfillRunner, which already runs
    /// on every startup and only ever fills nulls, computes them on the very next boot with the
    /// running code's precision. Same mechanism ADR-008's precision upgrade used.
    /// </para>
    /// <para>
    /// Status and UpdatedAt are never touched: relocating a listing is not an edit and must not
    /// re-trigger moderation.
    /// </para>
    /// </remarks>
    public partial class AddUserHomePoint : Migration
    {
        /// <summary>
        /// The three DATA statements of <see cref="Up"/>, verbatim, as the only copy of them.
        /// </summary>
        /// <remarks>
        /// Named constants rather than inline strings for one reason: re-application has to be
        /// TESTABLE. EF will not run a migration twice (it is in <c>__EFMigrationsHistory</c>), and
        /// going Up → Down → Up tests something else entirely — Down drops the tape, so the second
        /// Up inserts into an empty table and the <c>NOT EXISTS</c> guard in step 1 is never
        /// exercised at all. A test that re-executes these statements against a POPULATED tape is
        /// the only way to pin what a retried deploy, a container restart mid-rollout or an ADR-023
        /// rehearsal against a restored backup actually does, so the SQL has to be reachable from a
        /// test without being duplicated into it.
        /// </remarks>
        internal static class DataSql
        {
            /// <summary>Step 1 — capture every listing's current location. The undo tape.</summary>
            internal const string CaptureListingLocations =
                """
                INSERT INTO [ListingLocationsBeforeHomePoint]
                    ([ListingId], [Latitude], [Longitude], [DistrictId], [City], [Country], [CapturedAt])
                SELECT
                    l.[Id], l.[Latitude], l.[Longitude], l.[DistrictId], l.[City], l.[Country], SYSUTCDATETIME()
                FROM [Listings] AS l
                WHERE NOT EXISTS (
                    SELECT 1 FROM [ListingLocationsBeforeHomePoint] AS b WHERE b.[ListingId] = l.[Id]);
                """;

            /// <summary>Step 2 — derive each owner's home point from their newest located listing.</summary>
            internal const string DeriveOwnerHomePoints =
                """
                WITH [NewestLocatedListing] AS (
                    SELECT
                        l.[OwnerId],
                        l.[Latitude],
                        l.[Longitude],
                        ROW_NUMBER() OVER (
                            PARTITION BY l.[OwnerId]
                            ORDER BY l.[CreatedAt] DESC, l.[Id] DESC) AS [RowNumber]
                    FROM [Listings] AS l
                    WHERE l.[Latitude] IS NOT NULL AND l.[Longitude] IS NOT NULL)
                UPDATE u
                SET u.[HomeLatitude] = n.[Latitude],
                    u.[HomeLongitude] = n.[Longitude],
                    u.[HomePointUpdatedAt] = SYSUTCDATETIME()
                FROM [Users] AS u
                INNER JOIN [NewestLocatedListing] AS n
                    ON n.[OwnerId] = u.[Id] AND n.[RowNumber] = 1
                WHERE u.[HomeLatitude] IS NULL;
                """;

            /// <summary>Step 3 — collapse every listing onto its owner's home point.</summary>
            internal const string CollapseListingsOntoHomePoint =
                """
                WITH [NewestLocatedListing] AS (
                    SELECT
                        l.[OwnerId],
                        b.[City],
                        b.[Country],
                        ROW_NUMBER() OVER (
                            PARTITION BY l.[OwnerId]
                            ORDER BY l.[CreatedAt] DESC, l.[Id] DESC) AS [RowNumber]
                    FROM [ListingLocationsBeforeHomePoint] AS b
                    INNER JOIN [Listings] AS l ON l.[Id] = b.[ListingId]
                    WHERE b.[Latitude] IS NOT NULL AND b.[Longitude] IS NOT NULL)
                UPDATE l
                SET l.[Latitude] = u.[HomeLatitude],
                    l.[Longitude] = u.[HomeLongitude],
                    l.[City] = COALESCE(n.[City], l.[City]),
                    l.[Country] = COALESCE(n.[Country], l.[Country]),
                    l.[PublicLatitude] = NULL,
                    l.[PublicLongitude] = NULL,
                    l.[DistrictId] = NULL
                FROM [Listings] AS l
                INNER JOIN [Users] AS u ON u.[Id] = l.[OwnerId]
                LEFT JOIN [NewestLocatedListing] AS n
                    ON n.[OwnerId] = l.[OwnerId] AND n.[RowNumber] = 1
                WHERE u.[HomeLatitude] IS NOT NULL
                  AND u.[HomeLongitude] IS NOT NULL
                  AND (l.[Latitude] IS NULL
                       OR l.[Longitude] IS NULL
                       OR l.[Latitude] <> u.[HomeLatitude]
                       OR l.[Longitude] <> u.[HomeLongitude]
                       OR l.[City] <> COALESCE(n.[City], l.[City])
                       OR l.[Country] <> COALESCE(n.[Country], l.[Country]));
                """;

            /// <summary>Down's restore — the tape written back onto the listings.</summary>
            internal const string RestoreListingLocations =
                """
                UPDATE l
                SET l.[Latitude] = b.[Latitude],
                    l.[Longitude] = b.[Longitude],
                    l.[DistrictId] = b.[DistrictId],
                    l.[City] = b.[City],
                    l.[Country] = b.[Country],
                    l.[PublicLatitude] = NULL,
                    l.[PublicLongitude] = NULL
                FROM [Listings] AS l
                INNER JOIN [ListingLocationsBeforeHomePoint] AS b ON b.[ListingId] = l.[Id];
                """;
        }

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "HomeDistrictId",
                table: "Users",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "HomeLatitude",
                table: "Users",
                type: "decimal(9,6)",
                precision: 9,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "HomeLongitude",
                table: "Users",
                type: "decimal(9,6)",
                precision: 9,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "HomePointUpdatedAt",
                table: "Users",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "HomePublicLatitude",
                table: "Users",
                type: "decimal(9,6)",
                precision: 9,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "HomePublicLongitude",
                table: "Users",
                type: "decimal(9,6)",
                precision: 9,
                scale: 6,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ListingLocationsBeforeHomePoint",
                columns: table => new
                {
                    ListingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Latitude = table.Column<decimal>(type: "decimal(9,6)", precision: 9, scale: 6, nullable: true),
                    Longitude = table.Column<decimal>(type: "decimal(9,6)", precision: 9, scale: 6, nullable: true),
                    DistrictId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    City = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Country = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    CapturedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ListingLocationsBeforeHomePoint", x => x.ListingId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Users_HomeDistrictId",
                table: "Users",
                column: "HomeDistrictId");

            migrationBuilder.AddForeignKey(
                name: "FK_Users_Districts_HomeDistrictId",
                table: "Users",
                column: "HomeDistrictId",
                principalTable: "Districts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // ---- 1. Capture every listing's current location. The undo tape for steps 2 and 3. ----
            // NOT EXISTS makes a re-run a no-op instead of a primary-key violation, and — more
            // importantly — makes it impossible for a second run to overwrite genuine pre-migration
            // values with the already-collapsed ones.
            migrationBuilder.Sql(DataSql.CaptureListingLocations);

            // ---- 2. Derive each owner's home point from their newest listing that has coordinates ----
            // Newest by CreatedAt, with Id as a deterministic tie-breaker so the result does not
            // depend on row order. HomePublicLatitude/HomePublicLongitude/HomeDistrictId stay NULL
            // on purpose — see the class remarks; the startup backfill runner fills them.
            //
            // WHERE u.[HomeLatitude] IS NULL is what makes this step re-runnable: an owner who
            // already has a home point is skipped, so a second run can never move a point the app
            // has since written.
            migrationBuilder.Sql(DataSql.DeriveOwnerHomePoints);

            // ---- 3. Collapse every listing onto its owner's home point ----
            // THE LOSSY STEP: an owner whose listings sat in different districts now has them all on
            // one pin. Accepted deliberately (the home point is the product rule from here on), and
            // recoverable only from ListingLocationsBeforeHomePoint above.
            //
            // The derived columns are NULLed rather than copied, so the backfill runner recomputes
            // them from the new exact point instead of leaving stale values from the old one.
            // Status/UpdatedAt are untouched: this is a relocation, not an edit.
            //
            // City/Country move WITH the coordinates, taken from the same listing step 2 derived the
            // home point from — otherwise a relocated listing keeps a City that no longer names
            // where its pin is, and City is an exact-match public filter (see the class remarks'
            // invariant).
            //
            // WHAT MAKES THIS STEP RE-RUNNABLE is the trailing WHERE: it skips any row already in
            // exactly the state this statement would leave it in. Without it, a second run re-NULLs
            // PublicLatitude/PublicLongitude/DistrictId and throws away what
            // ListingLocationBackfillRunner computed — the runner only ever FILLS nulls, so those
            // columns then stay null until the next boot. That is the whole fix; it is covered by
            // Re_Applying_Up_Against_A_Populated_Tape_Changes_No_Listing_Row.
            //
            // One consequence of it, stated rather than hidden: on the first run the listing step 2
            // took the home point from is already on that point with that City/Country, so it is
            // skipped and KEEPS its existing public pair and district instead of having them
            // recomputed. Those values were derived from the very coordinates the row still has, so
            // they are consistent; and leaving an already-consistent derived pair alone is how every
            // other row in the database is treated (the backfill fills nulls only).
            //
            // SEPARATELY, the CTE reads City/Country and its filter from the TAPE rather than from
            // [Listings]. This is about the window MEANING what it says, not about behaviour: over
            // live coordinates the filter `l.[Latitude] IS NOT NULL` matches every listing of the
            // owner once this step has run once — because this step is what gave them all
            // coordinates — so "the newest LOCATED listing" silently degrades into "the newest
            // listing". The tape holds the pre-collapse coordinates and this step never writes it,
            // so the winner stays the row the migration actually derived the home point from.
            //
            // Be precise about what that does NOT buy: with the WHERE above in place, sourcing the
            // window live is not observably different, and a test cannot tell the two apart. The
            // first run sets every listing of an owner to the winner's City/Country, so whichever
            // row a second run elects carries those same values — and the WHERE then excludes every
            // row anyway. It is kept because an expression whose stated filter has become vacuous is
            // a trap for the next reader, not because it changes an outcome.
            //
            // COALESCE covers the one shape with no source listing: an owner who already has a home
            // point while none of their listings has coordinates. The CTE has no row for them, and
            // their City/Country are left exactly as found rather than nulled.
            migrationBuilder.Sql(DataSql.CollapseListingsOntoHomePoint);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Restore FIRST, while the snapshot table still exists — DropTable below destroys it.
            // Unchanged by the City/Country addition to step 3: the tape has always captured City
            // and Country, and this UPDATE has always written both back, so Up's extra writes are
            // already covered here.
            // PublicLatitude/PublicLongitude are NULLed rather than restored: the snapshot never
            // held them (they are derived, not authored), and the startup backfill runner recomputes
            // them from the restored exact point on the next boot.
            //
            // A listing created AFTER this migration has no snapshot row, so the INNER JOIN leaves
            // it alone: it keeps the home-point location it was created with, which is the only
            // location it has ever had. (If step 1 has since run a SECOND time it will have captured
            // such a listing too — with the location it already had, so restoring it is a no-op
            // either way.)
            migrationBuilder.Sql(DataSql.RestoreListingLocations);

            migrationBuilder.DropForeignKey(
                name: "FK_Users_Districts_HomeDistrictId",
                table: "Users");

            migrationBuilder.DropTable(
                name: "ListingLocationsBeforeHomePoint");

            migrationBuilder.DropIndex(
                name: "IX_Users_HomeDistrictId",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "HomeDistrictId",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "HomeLatitude",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "HomeLongitude",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "HomePointUpdatedAt",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "HomePublicLatitude",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "HomePublicLongitude",
                table: "Users");
        }
    }
}
