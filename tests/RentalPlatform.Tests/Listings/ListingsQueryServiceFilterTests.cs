using RentalPlatform.Application.DTOs;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Infrastructure.Services;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Listings;

// Covers the three server-side filters (search, age overlap, distance box) that the UI already
// sent but the backend silently dropped — same class of contract drift as M-020. No entity/schema
// change: all three reuse existing columns (Title/Description, AgeFromMonths/AgeToMonths,
// PublicLatitude/PublicLongitude).
public sealed class ListingsQueryServiceFilterTests
{
    private static readonly Guid OwnerId = new("a0000000-0000-0000-0000-000000000001");
    private static readonly Guid CategoryId = new("a0000000-0000-0000-0000-000000000002");

    private static async Task<SqliteTestDatabase> SeedBaseAsync()
    {
        var db = new SqliteTestDatabase();
        await db.SeedAsync(
            TestData.User(OwnerId, "owner@test.local"),
            TestData.Category(CategoryId));
        return db;
    }

    private static Listing Build(Guid id, string title, string description)
    {
        var listing = TestData.Listing(id, OwnerId, CategoryId);
        listing.Title = title;
        listing.Description = description;
        return listing;
    }

    // ---------- Search ----------

    [Fact]
    public async Task Search_Matches_On_Title()
    {
        using var db = await SeedBaseAsync();
        var matchId = new Guid("a0000000-0000-0000-0000-000000000010");
        var otherId = new Guid("a0000000-0000-0000-0000-000000000011");
        await db.SeedAsync(
            Build(matchId, "LEGO Duplo Starter Set", "A generic description."),
            Build(otherId, "Wooden Train Set", "A generic description."));

        await using var context = db.CreateContext();
        var result = await new ListingsQueryService(context).GetApprovedListingsAsync(
            new ListingsQueryFilter { Search = "Duplo" });

        var ids = result.Items.Select(i => i.Id).ToList();
        Assert.Single(ids);
        Assert.Contains(matchId, ids);
    }

    [Fact]
    public async Task Search_Matches_On_Description()
    {
        using var db = await SeedBaseAsync();
        var matchId = new Guid("a0000000-0000-0000-0000-000000000012");
        var otherId = new Guid("a0000000-0000-0000-0000-000000000013");
        await db.SeedAsync(
            Build(matchId, "Some Toy", "Great for building fine motor skills."),
            Build(otherId, "Another Toy", "Nothing relevant here."));

        await using var context = db.CreateContext();
        var result = await new ListingsQueryService(context).GetApprovedListingsAsync(
            new ListingsQueryFilter { Search = "motor skills" });

        var ids = result.Items.Select(i => i.Id).ToList();
        Assert.Single(ids);
        Assert.Contains(matchId, ids);
    }

    [Fact]
    public async Task Search_Is_Case_Insensitive()
    {
        using var db = await SeedBaseAsync();
        var matchId = new Guid("a0000000-0000-0000-0000-000000000014");
        await db.SeedAsync(Build(matchId, "LEGO Duplo Starter Set", "A generic description."));

        await using var context = db.CreateContext();
        var result = await new ListingsQueryService(context).GetApprovedListingsAsync(
            new ListingsQueryFilter { Search = "duplo" });

        Assert.Single(result.Items);
        Assert.Equal(matchId, result.Items.Single().Id);
    }

    [Fact]
    public async Task Blank_Search_Returns_All()
    {
        using var db = await SeedBaseAsync();
        var firstId = new Guid("a0000000-0000-0000-0000-000000000015");
        var secondId = new Guid("a0000000-0000-0000-0000-000000000016");
        await db.SeedAsync(
            Build(firstId, "LEGO Duplo Starter Set", "A generic description."),
            Build(secondId, "Wooden Train Set", "Another description."));

        await using var context = db.CreateContext();
        var result = await new ListingsQueryService(context).GetApprovedListingsAsync(
            new ListingsQueryFilter { Search = "   " });

        Assert.Equal(2, result.Items.Count);
    }

    // ---------- Age overlap ----------

    [Fact]
    public async Task Age_Overlap_Includes_Listing_Whose_Range_Overlaps_Requested_Window()
    {
        using var db = await SeedBaseAsync();
        var id = new Guid("a0000000-0000-0000-0000-000000000020");
        var listing = Build(id, "Overlapping Toy", "Desc");
        listing.AgeFromMonths = 24;
        listing.AgeToMonths = 48;
        await db.SeedAsync(listing);

        await using var context = db.CreateContext();
        var result = await new ListingsQueryService(context).GetApprovedListingsAsync(
            new ListingsQueryFilter { AgeFromMonths = 36, AgeToMonths = 60 });

        Assert.Single(result.Items);
        Assert.Equal(id, result.Items.Single().Id);
    }

    [Fact]
    public async Task Age_Overlap_Excludes_Listing_Entirely_Outside_Requested_Window()
    {
        using var db = await SeedBaseAsync();
        var id = new Guid("a0000000-0000-0000-0000-000000000021");
        var listing = Build(id, "Toddler Toy", "Desc");
        listing.AgeFromMonths = 0;
        listing.AgeToMonths = 12;
        await db.SeedAsync(listing);

        await using var context = db.CreateContext();
        var result = await new ListingsQueryService(context).GetApprovedListingsAsync(
            new ListingsQueryFilter { AgeFromMonths = 36, AgeToMonths = 60 });

        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task Age_Overlap_Includes_Listing_With_Null_Age_Bounds()
    {
        using var db = await SeedBaseAsync();
        var id = new Guid("a0000000-0000-0000-0000-000000000022");
        var listing = Build(id, "Unspecified Age Toy", "Desc");
        listing.AgeFromMonths = null;
        listing.AgeToMonths = null;
        await db.SeedAsync(listing);

        await using var context = db.CreateContext();
        var result = await new ListingsQueryService(context).GetApprovedListingsAsync(
            new ListingsQueryFilter { AgeFromMonths = 36, AgeToMonths = 60 });

        Assert.Single(result.Items);
        Assert.Equal(id, result.Items.Single().Id);
    }

    [Fact]
    public async Task Age_Overlap_Handles_120_Plus_Open_Ended_Request()
    {
        using var db = await SeedBaseAsync();
        var matchId = new Guid("a0000000-0000-0000-0000-000000000023");
        var excludedId = new Guid("a0000000-0000-0000-0000-000000000024");

        var matching = Build(matchId, "Older Kids Toy", "Desc");
        matching.AgeFromMonths = 100;
        matching.AgeToMonths = null; // open-ended upper on the listing side too

        var excluded = Build(excludedId, "Infant Toy", "Desc");
        excluded.AgeFromMonths = 0;
        excluded.AgeToMonths = 12;

        await db.SeedAsync(matching, excluded);

        await using var context = db.CreateContext();
        var result = await new ListingsQueryService(context).GetApprovedListingsAsync(
            new ListingsQueryFilter { AgeFromMonths = 120, AgeToMonths = null });

        Assert.Single(result.Items);
        Assert.Equal(matchId, result.Items.Single().Id);
    }

    // ---------- Distance ----------

    [Fact]
    public async Task Distance_Includes_Listing_With_Public_Coords_Inside_Box()
    {
        using var db = await SeedBaseAsync();
        var id = new Guid("a0000000-0000-0000-0000-000000000030");
        var listing = Build(id, "Nearby Toy", "Desc");
        listing.PublicLatitude = 40.19m;
        listing.PublicLongitude = 44.52m;
        await db.SeedAsync(listing);

        await using var context = db.CreateContext();
        var result = await new ListingsQueryService(context).GetApprovedListingsAsync(
            new ListingsQueryFilter { OriginLat = 40.1872m, OriginLng = 44.5152m, RadiusKm = 5.0 });

        Assert.Single(result.Items);
        Assert.Equal(id, result.Items.Single().Id);
    }

    [Fact]
    public async Task Distance_Excludes_Listing_With_Public_Coords_Outside_Box()
    {
        using var db = await SeedBaseAsync();
        var id = new Guid("a0000000-0000-0000-0000-000000000031");
        var listing = Build(id, "Far Away Toy", "Desc");
        // Roughly 100km+ away.
        listing.PublicLatitude = 41.20m;
        listing.PublicLongitude = 45.60m;
        await db.SeedAsync(listing);

        await using var context = db.CreateContext();
        var result = await new ListingsQueryService(context).GetApprovedListingsAsync(
            new ListingsQueryFilter { OriginLat = 40.1872m, OriginLng = 44.5152m, RadiusKm = 5.0 });

        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task Distance_Excludes_Listing_With_Null_Public_Coords_When_Distance_Filter_Active()
    {
        using var db = await SeedBaseAsync();
        var id = new Guid("a0000000-0000-0000-0000-000000000032");
        var listing = Build(id, "No Coords Toy", "Desc");
        listing.PublicLatitude = null;
        listing.PublicLongitude = null;
        await db.SeedAsync(listing);

        await using var context = db.CreateContext();
        var result = await new ListingsQueryService(context).GetApprovedListingsAsync(
            new ListingsQueryFilter { OriginLat = 40.1872m, OriginLng = 44.5152m, RadiusKm = 5.0 });

        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task Distance_TotalCount_Reflects_Filtered_Predicate()
    {
        using var db = await SeedBaseAsync();
        var nearId = new Guid("a0000000-0000-0000-0000-000000000033");
        var farId = new Guid("a0000000-0000-0000-0000-000000000034");

        var near = Build(nearId, "Nearby Toy", "Desc");
        near.PublicLatitude = 40.19m;
        near.PublicLongitude = 44.52m;

        var far = Build(farId, "Far Away Toy", "Desc");
        far.PublicLatitude = 41.20m;
        far.PublicLongitude = 45.60m;

        await db.SeedAsync(near, far);

        await using var context = db.CreateContext();
        var result = await new ListingsQueryService(context).GetApprovedListingsAsync(
            new ListingsQueryFilter { OriginLat = 40.1872m, OriginLng = 44.5152m, RadiusKm = 5.0 });

        Assert.Equal(1, result.TotalCount);
        Assert.Single(result.Items);
        Assert.Equal(nearId, result.Items.Single().Id);
    }
}
