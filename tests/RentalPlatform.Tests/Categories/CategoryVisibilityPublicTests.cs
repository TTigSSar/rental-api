using RentalPlatform.Application.DTOs;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Infrastructure.Services;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Categories;

// Admin console Phase 2, narrowed by human review: hiding a category (IsVisible = false) retires
// the LABEL, not the inventory. GET /api/categories excludes it; explicitly filtering by its id
// returns no results (the category isn't offered, so filtering by it isn't a supported path); but
// general browse/search with no category filter, the map-pins endpoint, and every home-page
// section still include its listings. Direct-link access stays unfiltered either way.
public sealed class CategoryVisibilityPublicTests
{
    private static readonly Guid OwnerId = new("f0000000-0000-0000-0000-000000000001");
    private static readonly Guid VisibleCategoryId = new("f0000000-0000-0000-0000-000000000002");
    private static readonly Guid HiddenCategoryId = new("f0000000-0000-0000-0000-000000000003");

    private static async Task<SqliteTestDatabase> SeedAsync()
    {
        var db = new SqliteTestDatabase();
        await db.SeedAsync(
            TestData.User(OwnerId, "owner@test.local"),
            TestData.Category(VisibleCategoryId, name: "Visible Cat", slug: "visible-cat", displayOrder: 1, isVisible: true),
            TestData.Category(HiddenCategoryId, name: "Hidden Cat", slug: "hidden-cat", displayOrder: 2, isVisible: false));
        return db;
    }

    [Fact]
    public async Task CategoriesQueryService_Excludes_Hidden_Categories()
    {
        using var db = await SeedAsync();

        await using var context = db.CreateContext();
        var categories = await new CategoriesQueryService(context).GetAllAsync();

        var ids = categories.Select(c => c.Id).ToList();
        Assert.Contains(VisibleCategoryId, ids);
        Assert.DoesNotContain(HiddenCategoryId, ids);
    }

    [Fact]
    public async Task ListingsQueryService_Unfiltered_Browse_Includes_Listings_In_Hidden_Category()
    {
        using var db = await SeedAsync();
        var visibleListingId = new Guid("f0000000-0000-0000-0000-000000000010");
        var hiddenListingId = new Guid("f0000000-0000-0000-0000-000000000011");
        await db.SeedAsync(
            TestData.Listing(visibleListingId, OwnerId, VisibleCategoryId, ListingStatus.Approved),
            TestData.Listing(hiddenListingId, OwnerId, HiddenCategoryId, ListingStatus.Approved));

        await using var context = db.CreateContext();
        var result = await new ListingsQueryService(context).GetApprovedListingsAsync(new ListingsQueryFilter());

        var ids = result.Items.Select(i => i.Id).ToList();
        Assert.Contains(visibleListingId, ids);
        Assert.Contains(hiddenListingId, ids);
    }

    [Fact]
    public async Task ListingsQueryService_MapPins_Unfiltered_Includes_Listings_In_Hidden_Category()
    {
        using var db = await SeedAsync();
        var visibleListingId = new Guid("f0000000-0000-0000-0000-000000000014");
        var hiddenListingId = new Guid("f0000000-0000-0000-0000-000000000015");
        var visibleListing = TestData.Listing(visibleListingId, OwnerId, VisibleCategoryId, ListingStatus.Approved);
        visibleListing.PublicLatitude = 40.18m;
        visibleListing.PublicLongitude = 44.51m;
        var hiddenListing = TestData.Listing(hiddenListingId, OwnerId, HiddenCategoryId, ListingStatus.Approved);
        hiddenListing.PublicLatitude = 40.18m;
        hiddenListing.PublicLongitude = 44.51m;
        await db.SeedAsync(visibleListing, hiddenListing);

        await using var context = db.CreateContext();
        var result = await new ListingsQueryService(context).GetMapPinsAsync(new ListingsQueryFilter());

        var ids = result.Items.Select(i => i.Id).ToList();
        Assert.Contains(visibleListingId, ids);
        Assert.Contains(hiddenListingId, ids);
    }

    [Fact]
    public async Task ListingsQueryService_Explicit_CategoryId_Filter_On_Hidden_Category_Returns_Empty()
    {
        using var db = await SeedAsync();
        var hiddenListingId = new Guid("f0000000-0000-0000-0000-000000000012");
        await db.SeedAsync(TestData.Listing(hiddenListingId, OwnerId, HiddenCategoryId, ListingStatus.Approved));

        await using var context = db.CreateContext();
        var result = await new ListingsQueryService(context).GetApprovedListingsAsync(
            new ListingsQueryFilter { CategoryId = HiddenCategoryId });

        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task ListingsQueryService_Direct_Link_Detail_Still_Reachable_For_Hidden_Category_Listing()
    {
        using var db = await SeedAsync();
        var hiddenListingId = new Guid("f0000000-0000-0000-0000-000000000013");
        await db.SeedAsync(TestData.Listing(hiddenListingId, OwnerId, HiddenCategoryId, ListingStatus.Approved));

        await using var context = db.CreateContext();
        var detail = await new ListingsQueryService(context).GetApprovedListingByIdAsync(hiddenListingId);

        Assert.NotNull(detail);
        Assert.Equal(hiddenListingId, detail!.Id);
    }

    [Fact]
    public async Task HomeSectionsQueryService_Includes_Listings_In_Hidden_Category()
    {
        using var db = await SeedAsync();
        var visibleListingId = new Guid("f0000000-0000-0000-0000-000000000020");
        var hiddenListingId = new Guid("f0000000-0000-0000-0000-000000000021");
        await db.SeedAsync(
            TestData.Listing(visibleListingId, OwnerId, VisibleCategoryId, ListingStatus.Approved),
            TestData.Listing(hiddenListingId, OwnerId, HiddenCategoryId, ListingStatus.Approved));

        await using var context = db.CreateContext();
        var sections = await new HomeSectionsQueryService(context).GetSectionsAsync(itemsPerSection: 12);

        var recentIds = sections.Sections.Single(s => s.Key == "recent").Items.Select(i => i.Id).ToList();
        Assert.Contains(visibleListingId, recentIds);
        Assert.Contains(hiddenListingId, recentIds);
    }
}
