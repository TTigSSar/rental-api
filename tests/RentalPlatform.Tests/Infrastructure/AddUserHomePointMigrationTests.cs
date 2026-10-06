using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using RentalPlatform.Infrastructure.Persistence.Migrations;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Infrastructure;

// The AddUserHomePoint migration's DATA steps, against a real SQL Server — the same style as
// ConversationsStoreModerationConcurrencyTests' migration test, and for the same reason: the logic
// under test is T-SQL (a windowed ROW_NUMBER, two UPDATE ... FROM joins), so running it anywhere
// but SQL Server would be testing a different program.
//
// This migration is the one that cannot be undone from the listings themselves: it collapses many
// distinct listing locations onto one home point per owner. The backup table is the only way back,
// so "Down restores what Up captured" is one of the two load-bearing assertions here.
//
// The other is "a relocation is not an edit": Status and UpdatedAt must survive Up and Down
// untouched, or an approved, publicly visible listing silently re-enters moderation on deploy. That
// property lives in T-SQL, so only a test at this level can see it.
//
// Requires a SQL Server reachable the way SqlServerTestDatabase describes. If there is none, every
// test here is SKIPPED with a reason ([SqlServerFact]) rather than failing: the suite has to stay
// runnable on a machine without SQL Server, and a red test for a missing dependency teaches people
// to ignore red. It must never be reported as PASSED, though - these tests guard the one step of
// this migration that cannot be undone from the listings themselves, and a silent pass there is
// green coverage that did not run.
public sealed class AddUserHomePointMigrationTests
{
    // The migration immediately before AddUserHomePoint. Seeding against this schema state is done
    // with raw SQL (SeedLegacy*), never through today's entity model, which would write the very
    // columns this migration is about to add.
    private const string MigrationBeforeHomePoint = "20260917052338_RenameDepositAmountToCompensationAmount";

    private static readonly Guid OwnerId = new("c1000000-0000-0000-0000-000000000001");
    private static readonly Guid OtherOwnerId = new("c1000000-0000-0000-0000-000000000002");
    private static readonly Guid CategoryId = new("c1000000-0000-0000-0000-000000000003");

    // An owner every one of whose listings predates coordinates entirely. Step 2's ROW_NUMBER has
    // nothing to select for them, so they must come out of the migration with NO home point — and
    // step 3, keyed off that home point, must then leave their listings' NULL location alone.
    private static readonly Guid NoCoordinatesOwnerId = new("c1000000-0000-0000-0000-000000000004");

    // An owner whose listings sat in DIFFERENT cities, which the pre-home-point schema allowed:
    // City/Country were authored per listing. Step 3 pins all of them on the home point, so City
    // has to travel with the coordinates — otherwise the Gyumri and Tbilisi rows stay filterable
    // only under a city they are no longer in. The home point comes from the newest LOCATED
    // listing, which here is MixedCityHomeSourceListingId (Yerevan/Armenia).
    private static readonly Guid MixedCityOwnerId = new("c1000000-0000-0000-0000-000000000005");

    // THE shape step 3's re-application bug needs, and the one real data certainly contains: one
    // owner with BOTH a located listing and a coordinate-less one. Step 3 gives the coordinate-less
    // row the home point, so from the second run onwards a window filtered on
    // `Listings.Latitude IS NOT NULL` sees BOTH as candidates — and this row is the NEWER of the
    // two, so it would win and City/Country would be rewritten from it. Seeded with a City of its
    // own ("Gyumri") so that rewrite is observable rather than an invisible same-value write.
    private static readonly Guid MixedCoordinatesOwnerId = new("c1000000-0000-0000-0000-000000000006");
    private static readonly Guid MixedCoordinatesLocatedListingId = new("c1000000-0000-0000-0000-000000000013");
    private static readonly Guid MixedCoordinatesUnlocatedListingId = new("c1000000-0000-0000-0000-000000000014");

    private const decimal MixedCoordinatesLatitude = 40.154300m;
    private const decimal MixedCoordinatesLongitude = 44.481900m;

    private static readonly Guid MixedCityHomeSourceListingId = new("c1000000-0000-0000-0000-00000000000f");
    private static readonly Guid MixedCitySecondYerevanListingId = new("c1000000-0000-0000-0000-000000000010");
    private static readonly Guid MixedCityGyumriListingId = new("c1000000-0000-0000-0000-000000000011");
    private static readonly Guid MixedCityForeignListingId = new("c1000000-0000-0000-0000-000000000012");

    // Newest first: the migration must pick NewestListingId's coordinates as the owner's home.
    private static readonly Guid NewestListingId = new("c1000000-0000-0000-0000-00000000000a");
    private static readonly Guid OlderListingId = new("c1000000-0000-0000-0000-00000000000b");
    private static readonly Guid OtherOwnerListingId = new("c1000000-0000-0000-0000-00000000000c");
    private static readonly Guid NoCoordinatesListingId = new("c1000000-0000-0000-0000-00000000000d");
    private static readonly Guid SecondNoCoordinatesListingId = new("c1000000-0000-0000-0000-00000000000e");

    private const decimal NewestLatitude = 40.187400m;
    private const decimal NewestLongitude = 44.512800m;
    private const decimal OlderLatitude = 40.220700m;
    private const decimal OlderLongitude = 44.525300m;
    private const decimal OtherOwnerLatitude = 40.126800m;
    private const decimal OtherOwnerLongitude = 44.471100m;

    // Every one of the mixed-city owner's listings has its OWN point, so "they all ended up on the
    // home point" is an assertion about four distinct values becoming one, not about rows that
    // happened to match already. The Gyumri and Tbilisi coordinates are the real ones.
    private const decimal MixedCityHomeLatitude = 40.177200m;
    private const decimal MixedCityHomeLongitude = 44.503300m;
    private const decimal MixedCitySecondYerevanLatitude = 40.201500m;
    private const decimal MixedCitySecondYerevanLongitude = 44.489700m;
    private const decimal MixedCityGyumriLatitude = 40.789200m;
    private const decimal MixedCityGyumriLongitude = 43.847300m;
    private const decimal MixedCityForeignLatitude = 41.715100m;
    private const decimal MixedCityForeignLongitude = 44.827100m;

    // Raw Listings.Status values, deliberately spread so a migration that normalised, reset or
    // re-queued a status could not pass by coincidence. Approved(2) is the one that matters most:
    // a relocation that pushed it back to PendingApproval(1) would silently unpublish a live
    // listing, which is exactly the regression this file has to catch.
    private const int ApprovedStatus = 2;
    private const int DraftStatus = 0;
    private const int PendingApprovalStatus = 1;
    private const int RejectedStatus = 3;
    private const int ArchivedStatus = 4;

    [SqlServerFact]
    public async Task Up_Derives_Each_Owners_Home_From_Their_Newest_Located_Listing_And_Collapses_The_Rest()
    {
        using var db = new SqlServerTestDatabase();
        db.MigrateTo(MigrationBeforeHomePoint);
        await SeedPreMigrationAsync(db);

        db.MigrateToLatest();

        await using var verify = db.CreateContext();

        // The home point is the newest listing's coordinates — newest by CreatedAt, with Id as the
        // deterministic tie-breaker.
        var owner = await verify.Users.SingleAsync(user => user.Id == OwnerId);
        Assert.Equal(NewestLatitude, owner.HomeLatitude);
        Assert.Equal(NewestLongitude, owner.HomeLongitude);
        Assert.NotNull(owner.HomePointUpdatedAt);

        // The derived half is deliberately left for the startup runner — a geohash must never be
        // computed in SQL, and the district follows the same rule.
        Assert.Null(owner.HomePublicLatitude);
        Assert.Null(owner.HomePublicLongitude);
        Assert.Null(owner.HomeDistrictId);

        // Both of that owner's listings now sit on the home point — including the older one, whose
        // own location is gone from the Listings table (and survives only in the backup).
        var listings = await verify.Listings
            .Where(listing => listing.OwnerId == OwnerId)
            .ToListAsync();

        Assert.Equal(2, listings.Count);
        Assert.All(listings, listing =>
        {
            Assert.Equal(NewestLatitude, listing.Latitude);
            Assert.Equal(NewestLongitude, listing.Longitude);
            Assert.Null(listing.PublicLatitude);
            Assert.Null(listing.PublicLongitude);
            Assert.Null(listing.DistrictId);
        });

        // Partitioned per owner: the second owner got their own home point, not the first one's.
        var otherOwner = await verify.Users.SingleAsync(user => user.Id == OtherOwnerId);
        Assert.Equal(OtherOwnerLatitude, otherOwner.HomeLatitude);
    }

    [SqlServerFact]
    public async Task Up_Captures_Every_Listings_Pre_Migration_Location()
    {
        using var db = new SqlServerTestDatabase();
        db.MigrateTo(MigrationBeforeHomePoint);
        await SeedPreMigrationAsync(db);

        db.MigrateToLatest();

        await using var verify = db.CreateContext();
        var snapshots = await verify.ListingLocationsBeforeHomePoint.ToListAsync();

        // Every listing, not only the located ones — all eleven seeded rows.
        Assert.Equal(11, snapshots.Count);

        var older = snapshots.Single(snapshot => snapshot.ListingId == OlderListingId);
        Assert.Equal(OlderLatitude, older.Latitude);
        Assert.Equal(OlderLongitude, older.Longitude);
        Assert.Equal("Yerevan", older.City);
        Assert.Equal("Armenia", older.Country);
        Assert.NotEqual(default, older.CapturedAt);

        // A coordinate-less listing gets a tape row too, with NULL coordinates rather than no row.
        // It has to: Down's INNER JOIN is what restores a listing, and a listing missing from the
        // tape would be a listing Down silently skips. Observed on real data in the rehearsal.
        var withoutCoordinates = snapshots.Single(snapshot => snapshot.ListingId == NoCoordinatesListingId);
        Assert.Null(withoutCoordinates.Latitude);
        Assert.Null(withoutCoordinates.Longitude);
        Assert.Null(withoutCoordinates.DistrictId);
        Assert.Equal("Yerevan", withoutCoordinates.City);
        Assert.Equal("Armenia", withoutCoordinates.Country);
        Assert.NotEqual(default, withoutCoordinates.CapturedAt);

        // The tape holds the city each listing was AUTHORED with, not the one step 3 then wrote
        // over it. This is what makes the City/Country collapse reversible, so Down's restore is
        // only as good as these two rows.
        var gyumri = snapshots.Single(snapshot => snapshot.ListingId == MixedCityGyumriListingId);
        Assert.Equal("Gyumri", gyumri.City);
        Assert.Equal("Armenia", gyumri.Country);

        var foreign = snapshots.Single(snapshot => snapshot.ListingId == MixedCityForeignListingId);
        Assert.Equal("Tbilisi", foreign.City);
        Assert.Equal("Georgia", foreign.Country);
    }

    // Step 3 relocates a listing onto its owner's home point; its City has to travel with it. City
    // is an exact-match public filter (ListingsQueryService) while the pin comes from the home
    // point, so a listing left behind with its old City is invisible to ?city=<the city it is now
    // pinned in> and at the same time drawn on the map in that city — two public surfaces
    // disagreeing about one row. The trigger is not "an owner outside Yerevan": it is any listing
    // whose City differs from the City of its owner's newest located listing, which is exactly
    // MixedCityOwnerId's shape (a Gyumri and a Tbilisi listing beside the Yerevan ones).
    [SqlServerFact]
    public async Task Up_Moves_City_And_Country_Onto_The_Home_Point_Source_Listings_Values()
    {
        using var db = new SqlServerTestDatabase();
        db.MigrateTo(MigrationBeforeHomePoint);
        await SeedPreMigrationAsync(db);

        db.MigrateToLatest();

        await using var verify = db.CreateContext();

        // The home point really is the Yerevan listing's — so "the source listing's city" below
        // means Yerevan/Armenia and not whatever happened to be there.
        var owner = await verify.Users.SingleAsync(user => user.Id == MixedCityOwnerId);
        Assert.Equal(MixedCityHomeLatitude, owner.HomeLatitude);
        Assert.Equal(MixedCityHomeLongitude, owner.HomeLongitude);

        var listings = await verify.Listings
            .Where(listing => listing.OwnerId == MixedCityOwnerId)
            .ToListAsync();

        Assert.Equal(4, listings.Count);
        Assert.All(listings, listing =>
        {
            Assert.Equal(MixedCityHomeLatitude, listing.Latitude);
            Assert.Equal(MixedCityHomeLongitude, listing.Longitude);
            Assert.Equal("Yerevan", listing.City);
            Assert.Equal("Armenia", listing.Country);
        });

        // Named individually as well, so the assertion cannot be satisfied by the rows that were
        // already Yerevan/Armenia: these are the two that had to change, and the foreign one is
        // the only proof that Country moves too.
        Assert.Equal("Yerevan", listings.Single(listing => listing.Id == MixedCityGyumriListingId).City);
        Assert.Equal("Yerevan", listings.Single(listing => listing.Id == MixedCityForeignListingId).City);
        Assert.Equal("Armenia", listings.Single(listing => listing.Id == MixedCityForeignListingId).Country);

        // And it is scoped to the collapse: another owner's single-city listing is left alone.
        var otherOwnerListing = await verify.Listings
            .SingleAsync(listing => listing.Id == OtherOwnerListingId);
        Assert.Equal("Yerevan", otherOwnerListing.City);
        Assert.Equal("Armenia", otherOwnerListing.Country);
    }

    // The other untested real-data path: an owner with no coordinates anywhere. Step 2's ROW_NUMBER
    // sees no row for them, so they must come out with no home point at all — and step 3, which is
    // gated on that home point, must leave their listings' NULL location exactly as it found it
    // rather than collapsing them onto someone else's point or onto NULL-vs-NULL nonsense.
    [SqlServerFact]
    public async Task Up_Leaves_An_Owner_With_No_Located_Listings_Without_A_Home_Point()
    {
        using var db = new SqlServerTestDatabase();
        db.MigrateTo(MigrationBeforeHomePoint);
        await SeedPreMigrationAsync(db);

        db.MigrateToLatest();

        await using var verify = db.CreateContext();

        var owner = await verify.Users.SingleAsync(user => user.Id == NoCoordinatesOwnerId);
        Assert.Null(owner.HomeLatitude);
        Assert.Null(owner.HomeLongitude);
        Assert.Null(owner.HomePublicLatitude);
        Assert.Null(owner.HomePublicLongitude);
        Assert.Null(owner.HomeDistrictId);
        Assert.Null(owner.HomePointUpdatedAt);

        var listings = await verify.Listings
            .Where(listing => listing.OwnerId == NoCoordinatesOwnerId)
            .ToListAsync();

        Assert.Equal(2, listings.Count);
        Assert.All(listings, listing =>
        {
            Assert.Null(listing.Latitude);
            Assert.Null(listing.Longitude);
            Assert.Null(listing.PublicLatitude);
            Assert.Null(listing.PublicLongitude);
            Assert.Null(listing.DistrictId);
        });
    }

    // THE safety property of this migration: a relocation is not an edit. If the collapse step ever
    // stamps UpdatedAt or writes Status, an approved, publicly visible listing silently re-enters
    // moderation — or looks freshly edited to every surface that sorts or badges by UpdatedAt. The
    // service-level test (SetHomePoint_Leaves_Status_And_UpdatedAt_Untouched) proves the C# writer
    // behaves; nothing but this proves the T-SQL does.
    [SqlServerFact]
    public async Task Up_Leaves_Every_Listings_Status_And_UpdatedAt_Untouched()
    {
        using var db = new SqlServerTestDatabase();
        db.MigrateTo(MigrationBeforeHomePoint);
        await SeedPreMigrationAsync(db);

        await using (var beforeContext = db.CreateContext())
        {
            var before = await ReadListingModerationFieldsAsync(beforeContext);

            db.MigrateToLatest();

            await using var afterContext = db.CreateContext();
            var after = await ReadListingModerationFieldsAsync(afterContext);

            // Byte-for-byte, every row, in both directions of the comparison — a migration that
            // re-stamped only the collapsed rows, or only the approved ones, must still fail.
            Assert.Equal(before.Keys.OrderBy(id => id), after.Keys.OrderBy(id => id));
            foreach (var (listingId, expected) in before)
            {
                Assert.Equal(expected, after[listingId]);
            }
        }

        // And the values are the seeded ones, so the test cannot pass by both reads being equally
        // wrong (e.g. a migration that reset every status to the same value before the first read).
        await using var verify = db.CreateContext();
        var statuses = await ReadListingModerationFieldsAsync(verify);

        Assert.Equal(ApprovedStatus, statuses[NewestListingId].Status);
        Assert.Equal(DraftStatus, statuses[OlderListingId].Status);
        Assert.Equal(PendingApprovalStatus, statuses[OtherOwnerListingId].Status);
        Assert.Equal(RejectedStatus, statuses[NoCoordinatesListingId].Status);
        Assert.Equal(ArchivedStatus, statuses[SecondNoCoordinatesListingId].Status);

        // UpdatedAt is seeded days in the past; a SYSUTCDATETIME() stamp would land today.
        Assert.All(statuses.Values, fields =>
            Assert.True(
                fields.UpdatedAt < DateTime.UtcNow.AddDays(-1),
                $"UpdatedAt was re-stamped to {fields.UpdatedAt:O}, i.e. the migration treated a relocation as an edit."));
    }

    // Down has the same obligation as Up, and its own UPDATE ... FROM is just as easy to "helpfully"
    // extend with an UpdatedAt write. Rolling a deploy back must not re-queue anything for
    // moderation either.
    [SqlServerFact]
    public async Task Down_Leaves_Every_Listings_Status_And_UpdatedAt_Untouched()
    {
        using var db = new SqlServerTestDatabase();
        db.MigrateTo(MigrationBeforeHomePoint);
        await SeedPreMigrationAsync(db);

        db.MigrateToLatest();

        await using (var beforeContext = db.CreateContext())
        {
            var before = await ReadListingModerationFieldsAsync(beforeContext);

            db.MigrateTo(MigrationBeforeHomePoint); // Down

            await using var afterContext = db.CreateContext();
            var after = await ReadListingModerationFieldsAsync(afterContext);

            Assert.Equal(before.Keys.OrderBy(id => id), after.Keys.OrderBy(id => id));
            foreach (var (listingId, expected) in before)
            {
                Assert.Equal(expected, after[listingId]);
            }
        }
    }

    // The reason the backup table exists. Many points became one, and only this restores them.
    [SqlServerFact]
    public async Task Down_Restores_Every_Listings_Original_Location()
    {
        using var db = new SqlServerTestDatabase();
        db.MigrateTo(MigrationBeforeHomePoint);
        await SeedPreMigrationAsync(db);

        db.MigrateToLatest();
        db.MigrateTo(MigrationBeforeHomePoint); // Down

        await using var verify = db.CreateContext();

        // Read with raw SQL: today's entity model has Home* columns, which Down has just dropped.
        var restored = await ReadListingLocationsAsync(verify);

        Assert.Equal(NewestLatitude, restored[NewestListingId].Latitude);
        Assert.Equal(NewestLongitude, restored[NewestListingId].Longitude);
        Assert.Equal(OlderLatitude, restored[OlderListingId].Latitude);
        Assert.Equal(OlderLongitude, restored[OlderListingId].Longitude);
        Assert.Equal(OtherOwnerLatitude, restored[OtherOwnerListingId].Latitude);

        // The public pair is nulled rather than restored — it was never captured (it is derived,
        // not authored), and the startup backfill runner recomputes it on the next boot.
        Assert.Null(restored[OlderListingId].PublicLatitude);

        // City/Country are restored per listing too, including the two rows Up rewrote onto the
        // home point's city. A Down that restored only coordinates would leave a Gyumri listing
        // back at its Gyumri point while still labelled Yerevan — the same disagreement Up's extra
        // writes exist to prevent, just mirrored.
        Assert.Equal(MixedCityGyumriLatitude, restored[MixedCityGyumriListingId].Latitude);
        Assert.Equal(MixedCityGyumriLongitude, restored[MixedCityGyumriListingId].Longitude);
        Assert.Equal("Gyumri", restored[MixedCityGyumriListingId].City);
        Assert.Equal("Armenia", restored[MixedCityGyumriListingId].Country);

        Assert.Equal(MixedCityForeignLatitude, restored[MixedCityForeignListingId].Latitude);
        Assert.Equal("Tbilisi", restored[MixedCityForeignListingId].City);
        Assert.Equal("Georgia", restored[MixedCityForeignListingId].Country);

        // The rows whose city never changed come back unchanged as well.
        Assert.Equal(MixedCitySecondYerevanLatitude, restored[MixedCitySecondYerevanListingId].Latitude);
        Assert.Equal("Yerevan", restored[MixedCitySecondYerevanListingId].City);
        Assert.Equal("Yerevan", restored[MixedCityHomeSourceListingId].City);
    }

    // The owner the fixture used to have no shape for: BOTH a located listing and a coordinate-less
    // one. First-run behaviour, asserted here so the re-application tests below have a known
    // starting state — and so that the tape-sourced window in step 3 is pinned as producing exactly
    // the same first run as the [Listings]-sourced one it replaced.
    [SqlServerFact]
    public async Task Up_Gives_A_Coordinate_Less_Listing_Its_Owners_Home_Point_And_City()
    {
        using var db = new SqlServerTestDatabase();
        db.MigrateTo(MigrationBeforeHomePoint);
        await SeedPreMigrationAsync(db);

        db.MigrateToLatest();

        await using var verify = db.CreateContext();

        // The home point comes from the only listing that HAD coordinates, even though it is the
        // older of the two.
        var owner = await verify.Users.SingleAsync(user => user.Id == MixedCoordinatesOwnerId);
        Assert.Equal(MixedCoordinatesLatitude, owner.HomeLatitude);
        Assert.Equal(MixedCoordinatesLongitude, owner.HomeLongitude);

        var listings = await verify.Listings
            .Where(listing => listing.OwnerId == MixedCoordinatesOwnerId)
            .ToListAsync();

        Assert.Equal(2, listings.Count);
        Assert.All(listings, listing =>
        {
            Assert.Equal(MixedCoordinatesLatitude, listing.Latitude);
            Assert.Equal(MixedCoordinatesLongitude, listing.Longitude);
            // Both rows carry the located listing's City/Country — the previously coordinate-less
            // row is no longer labelled "Gyumri" while pinned in Yerevan.
            Assert.Equal("Yerevan", listing.City);
            Assert.Equal("Armenia", listing.Country);
        });

        // And the tape remembers what it really was, which is what makes it reversible.
        var tape = await verify.ListingLocationsBeforeHomePoint
            .SingleAsync(snapshot => snapshot.ListingId == MixedCoordinatesUnlocatedListingId);
        Assert.Null(tape.Latitude);
        Assert.Equal("Gyumri", tape.City);
    }

    // Migrations get re-run: a retried deploy, a rehearsal against a restored backup (ADR-023), a
    // container restart mid-rollout. A second Up must not overwrite the captured originals with the
    // already-collapsed values, which would silently destroy the only way back.
    //
    // Re-run by executing Up's DATA statements directly, against the tape as the first run left it.
    // The obvious alternative — Up, Down, Up — CANNOT test this: Down drops the tape table, so the
    // second Up inserts into an empty one and the WHERE NOT EXISTS guard is never reached. That
    // version of this test passed with the guard deleted, which is the definition of not testing it.
    [SqlServerFact]
    public async Task Re_Applying_Up_Against_A_Populated_Tape_Does_Not_Corrupt_The_Backup()
    {
        using var db = new SqlServerTestDatabase();
        db.MigrateTo(MigrationBeforeHomePoint);
        await SeedPreMigrationAsync(db);

        db.MigrateToLatest();

        await RunUpDataStepsAsync(db);

        await using var verify = db.CreateContext();
        var snapshots = await verify.ListingLocationsBeforeHomePoint.ToListAsync();

        // No duplicate rows (the tape's primary key would have thrown) and, the part that matters,
        // no row overwritten with the collapsed value the listing carries NOW.
        Assert.Equal(11, snapshots.Count);
        Assert.Equal(OlderLatitude, snapshots.Single(s => s.ListingId == OlderListingId).Latitude);
        Assert.Null(snapshots.Single(s => s.ListingId == NoCoordinatesListingId).Latitude);
        Assert.Equal("Gyumri", snapshots.Single(s => s.ListingId == MixedCityGyumriListingId).City);
        Assert.Equal("Tbilisi", snapshots.Single(s => s.ListingId == MixedCityForeignListingId).City);
        Assert.Null(snapshots.Single(s => s.ListingId == MixedCoordinatesUnlocatedListingId).Latitude);
        Assert.Equal("Gyumri", snapshots.Single(s => s.ListingId == MixedCoordinatesUnlocatedListingId).City);
    }

    // The claim in this migration's remarks, now tested instead of asserted in prose: re-applying Up
    // changes NOTHING. It was true of steps 1–2 only. Step 3's single predicate was
    // `WHERE u.HomeLatitude IS NOT NULL`, so a second run rewrote every listing of every owner with
    // a home point — re-NULLing the public pair and district that ListingLocationBackfillRunner had
    // computed (it only ever fills nulls, so they stay null until the next startup), and electing
    // its City/Country winner from a window over `Listings.Latitude IS NOT NULL`, which by then
    // matched every listing of the owner because step 3 is what gave them coordinates.
    //
    // The derived trio is filled here first, standing in for the backfill having run between the two
    // applications — which is exactly what happens in production, where the runner executes on every
    // boot and the migration may be re-applied on a later one.
    [SqlServerFact]
    public async Task Re_Applying_Up_Against_A_Populated_Tape_Changes_No_Listing_Row()
    {
        using var db = new SqlServerTestDatabase();
        db.MigrateTo(MigrationBeforeHomePoint);
        await SeedPreMigrationAsync(db);

        db.MigrateToLatest();

        // Stand in for ListingLocationBackfillRunner's pass over the collapsed rows.
        Guid districtId;
        await using (var backfill = db.CreateContext())
        {
            districtId = (await backfill.Districts.OrderBy(district => district.Code).FirstAsync()).Id;
            await backfill.Database.ExecuteSqlRawAsync(
                """
                UPDATE [Listings]
                SET [PublicLatitude] = [Latitude],
                    [PublicLongitude] = [Longitude],
                    [DistrictId] = {0}
                WHERE [Latitude] IS NOT NULL;
                """,
                districtId);
        }

        Dictionary<Guid, (decimal? Latitude, decimal? Longitude, decimal? PublicLatitude, string City, string Country)> before;
        Dictionary<Guid, Guid?> districtsBefore;
        await using (var snapshotContext = db.CreateContext())
        {
            before = await ReadListingLocationsAsync(snapshotContext);
            districtsBefore = await snapshotContext.Listings
                .ToDictionaryAsync(listing => listing.Id, listing => listing.DistrictId);
        }

        await RunUpDataStepsAsync(db);

        await using var verify = db.CreateContext();
        var after = await ReadListingLocationsAsync(verify);
        var districtsAfter = await verify.Listings.ToDictionaryAsync(listing => listing.Id, listing => listing.DistrictId);

        Assert.Equal(before.Keys.OrderBy(id => id), after.Keys.OrderBy(id => id));
        foreach (var (listingId, expected) in before)
        {
            Assert.Equal(expected, after[listingId]);
            Assert.Equal(districtsBefore[listingId], districtsAfter[listingId]);
        }

        // Spelled out for the row the whole fixture gap was about, so a weakening of the comparison
        // above cannot quietly take this with it: the district the backfill computed survived, and
        // the City it was collapsed to did not drift back to the one it was authored with.
        Assert.Equal(districtId, districtsAfter[MixedCoordinatesUnlocatedListingId]);
        Assert.Equal("Yerevan", after[MixedCoordinatesUnlocatedListingId].City);
        Assert.Equal("Armenia", after[MixedCoordinatesUnlocatedListingId].Country);
        Assert.Equal("Yerevan", after[MixedCityGyumriListingId].City);
        Assert.Equal("Armenia", after[MixedCityForeignListingId].Country);
    }

    /// <summary>
    /// Runs Up's three DATA statements again, in order, against the schema the migration has already
    /// created — i.e. what a retried deploy or a hand-run ADR-023 rehearsal does to a database that
    /// has had this migration applied once.
    /// </summary>
    /// <remarks>
    /// The statements come from the migration itself (<see cref="AddUserHomePoint.DataSql"/>), never
    /// copied into this file: a copy would keep passing after the real SQL changed, which is the one
    /// failure mode a re-application test cannot afford.
    /// </remarks>
    private static async Task RunUpDataStepsAsync(SqlServerTestDatabase db)
    {
        await using var context = db.CreateContext();
        await context.Database.ExecuteSqlRawAsync(AddUserHomePoint.DataSql.CaptureListingLocations);
        await context.Database.ExecuteSqlRawAsync(AddUserHomePoint.DataSql.DeriveOwnerHomePoints);
        await context.Database.ExecuteSqlRawAsync(AddUserHomePoint.DataSql.CollapseListingsOntoHomePoint);
    }

    private static async Task SeedPreMigrationAsync(SqlServerTestDatabase db)
    {
        await db.SeedLegacyUsersAsync(
            (OwnerId, "owner@home-point-migration.test", 0),
            (OtherOwnerId, "other@home-point-migration.test", 0),
            (NoCoordinatesOwnerId, "no-coordinates@home-point-migration.test", 0),
            (MixedCityOwnerId, "mixed-city@home-point-migration.test", 0),
            (MixedCoordinatesOwnerId, "mixed-coordinates@home-point-migration.test", 0));

        await db.SeedLegacyCategoryAsync(CategoryId, "Building Blocks", $"building-blocks-{CategoryId:N}");

        // CreatedAt ordering is what the ROW_NUMBER partition keys off. Status and UpdatedAt are
        // pinned per listing so "the migration did not touch them" is an assertion about known
        // values rather than about whatever the seed happened to write.
        await db.SeedLegacyListingAsync(
            NewestListingId, OwnerId, CategoryId, NewestLatitude, NewestLongitude,
            createdDaysAgo: 1, status: ApprovedStatus, updatedDaysAgo: 40);
        await db.SeedLegacyListingAsync(
            OlderListingId, OwnerId, CategoryId, OlderLatitude, OlderLongitude,
            createdDaysAgo: 30, status: DraftStatus, updatedDaysAgo: 25);
        await db.SeedLegacyListingAsync(
            OtherOwnerListingId, OtherOwnerId, CategoryId, OtherOwnerLatitude, OtherOwnerLongitude,
            createdDaysAgo: 5, status: PendingApprovalStatus, updatedDaysAgo: 12);

        // The two paths SeedPreMigrationAsync used to leave untested, and both fired on real data in
        // the production-shaped rehearsal: an owner with no coordinates anywhere gets no home point,
        // and their coordinate-less listings still land on the undo tape.
        await db.SeedLegacyListingAsync(
            NoCoordinatesListingId, NoCoordinatesOwnerId, CategoryId, latitude: null, longitude: null,
            createdDaysAgo: 9, status: RejectedStatus, updatedDaysAgo: 9);
        await db.SeedLegacyListingAsync(
            SecondNoCoordinatesListingId, NoCoordinatesOwnerId, CategoryId, latitude: null, longitude: null,
            createdDaysAgo: 60, status: ArchivedStatus, updatedDaysAgo: 55);

        // The mixed-city owner: the newest located listing (the home-point source) is in Yerevan,
        // and the siblings step 3 collapses onto it are not — one in another Armenian city, one in
        // another country. Both City and Country therefore differ from the source's, so an Up that
        // moved only the coordinates leaves two rows naming a place they are not pinned in.
        await db.SeedLegacyListingAsync(
            MixedCityHomeSourceListingId, MixedCityOwnerId, CategoryId,
            MixedCityHomeLatitude, MixedCityHomeLongitude,
            createdDaysAgo: 2, status: ApprovedStatus, updatedDaysAgo: 33,
            city: "Yerevan", country: "Armenia");
        await db.SeedLegacyListingAsync(
            MixedCitySecondYerevanListingId, MixedCityOwnerId, CategoryId,
            MixedCitySecondYerevanLatitude, MixedCitySecondYerevanLongitude,
            createdDaysAgo: 11, status: ApprovedStatus, updatedDaysAgo: 28,
            city: "Yerevan", country: "Armenia");
        await db.SeedLegacyListingAsync(
            MixedCityGyumriListingId, MixedCityOwnerId, CategoryId,
            MixedCityGyumriLatitude, MixedCityGyumriLongitude,
            createdDaysAgo: 21, status: ApprovedStatus, updatedDaysAgo: 20,
            city: "Gyumri", country: "Armenia");
        await db.SeedLegacyListingAsync(
            MixedCityForeignListingId, MixedCityOwnerId, CategoryId,
            MixedCityForeignLatitude, MixedCityForeignLongitude,
            createdDaysAgo: 33, status: DraftStatus, updatedDaysAgo: 31,
            city: "Tbilisi", country: "Georgia");

        // The mixed-COORDINATES owner: one located listing and one with no coordinates at all. The
        // two previous owners are "all located" and "none located"; neither can show what step 3
        // does to a row it has just given coordinates to, and that is the row a re-run then treats
        // as a candidate for the City/Country window. The coordinate-less one is the NEWER of the
        // two (so it would win that window) and carries a different City (so winning it would be
        // visible).
        await db.SeedLegacyListingAsync(
            MixedCoordinatesLocatedListingId, MixedCoordinatesOwnerId, CategoryId,
            MixedCoordinatesLatitude, MixedCoordinatesLongitude,
            createdDaysAgo: 18, status: ApprovedStatus, updatedDaysAgo: 17,
            city: "Yerevan", country: "Armenia");
        await db.SeedLegacyListingAsync(
            MixedCoordinatesUnlocatedListingId, MixedCoordinatesOwnerId, CategoryId,
            latitude: null, longitude: null,
            createdDaysAgo: 4, status: ApprovedStatus, updatedDaysAgo: 3,
            city: "Gyumri", country: "Armenia");
    }

    /// <summary>
    /// Reads every listing's <c>Status</c>/<c>UpdatedAt</c> with raw SQL.
    /// </summary>
    /// <remarks>
    /// Raw SQL, not the entity model, because this is called on BOTH sides of a migration — before
    /// Up (schema without the Home* columns) and after Down (schema without them again). Reading
    /// Users through today's model in either state fails on "Invalid column name", and reading
    /// Listings through it only happens to work today.
    /// </remarks>
    private static async Task<Dictionary<Guid, (int Status, DateTime UpdatedAt)>>
        ReadListingModerationFieldsAsync(DbContext context)
    {
        var result = new Dictionary<Guid, (int, DateTime)>();

        await using var connection = new SqlConnection(context.Database.GetConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT [Id], [Status], [UpdatedAt] FROM [Listings];";

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result[reader.GetGuid(0)] = (reader.GetInt32(1), reader.GetDateTime(2));
        }

        return result;
    }

    private static async Task<Dictionary<Guid, (decimal? Latitude, decimal? Longitude, decimal? PublicLatitude, string City, string Country)>>
        ReadListingLocationsAsync(DbContext context)
    {
        var result = new Dictionary<Guid, (decimal?, decimal?, decimal?, string, string)>();

        await using var connection = new SqlConnection(context.Database.GetConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT [Id], [Latitude], [Longitude], [PublicLatitude], [City], [Country] FROM [Listings];";

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result[reader.GetGuid(0)] = (
                reader.IsDBNull(1) ? null : reader.GetDecimal(1),
                reader.IsDBNull(2) ? null : reader.GetDecimal(2),
                reader.IsDBNull(3) ? null : reader.GetDecimal(3),
                reader.GetString(4),
                reader.GetString(5));
        }

        return result;
    }
}
