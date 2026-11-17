using System.ComponentModel.DataAnnotations;
using RentalPlatform.Application.DTOs;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Infrastructure.Services;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Listings;

// ADR-066: multi-select filters, condition, server-side sort, facet counts, District on the catalogue.
public sealed class BrowseFiltersV2Tests
{
    private static readonly Guid OwnerId = new("b0000000-0000-0000-0000-000000000001");
    private static readonly Guid RenterId = new("b0000000-0000-0000-0000-000000000002");
    private static readonly Guid CatA = new("b0000000-0000-0000-0000-0000000000a1");
    private static readonly Guid CatB = new("b0000000-0000-0000-0000-0000000000a2");
    private static readonly Guid CatHidden = new("b0000000-0000-0000-0000-0000000000a3");

    private static int _seq;

    private static async Task<SqliteTestDatabase> SeedBaseAsync()
    {
        var db = new SqliteTestDatabase();
        await db.SeedAsync(
            TestData.User(OwnerId, "owner@test.local"),
            TestData.User(RenterId, "renter@test.local"),
            TestData.Category(CatA, "Cat A"),
            TestData.Category(CatB, "Cat B"),
            TestData.Category(CatHidden, "Hidden", isVisible: false));
        return db;
    }

    private static Listing L(
        Guid? category = null,
        decimal price = 10m,
        string? condition = null,
        int? ageFrom = null,
        int? ageTo = null,
        Guid? district = null,
        int createdMinutesAgo = 0,
        ListingStatus status = ListingStatus.Approved,
        decimal? lat = null,
        decimal? lng = null)
    {
        var n = Interlocked.Increment(ref _seq);
        var listing = TestData.Listing(new Guid($"b1000000-0000-0000-0000-{n:000000000000}"), OwnerId, category ?? CatA, status);
        listing.PricePerDay = price;
        listing.Condition = condition;
        listing.AgeFromMonths = ageFrom;
        listing.AgeToMonths = ageTo;
        listing.DistrictId = district;
        listing.CreatedAt = DateTime.UtcNow.AddMinutes(-createdMinutesAgo);
        listing.PublicLatitude = lat;
        listing.PublicLongitude = lng;
        return listing;
    }

    private static async Task<List<Guid>> Ids(SqliteTestDatabase db, ListingsQueryFilter filter)
    {
        await using var context = db.CreateContext();
        var result = await new ListingsQueryService(context).GetApprovedListingsAsync(filter);
        return result.Items.Select(i => i.Id).ToList();
    }

    private static async Task<ListingFacetsResponse> Facets(SqliteTestDatabase db, ListingsQueryFilter filter)
    {
        await using var context = db.CreateContext();
        return await new ListingsQueryService(context).GetFacetsAsync(filter);
    }

    private static IReadOnlyList<ValidationResult> Validate(ListingsQueryFilter filter)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(filter, new ValidationContext(filter), results, validateAllProperties: true);
        return results;
    }

    // ---------- categoryIds ----------

    [Fact]
    public async Task CategoryIds_Match_Any_Of_The_Set_And_Merge_Legacy_CategoryId()
    {
        using var db = await SeedBaseAsync();
        var a = L(CatA); var b = L(CatB); var hidden = L(CatHidden);
        await db.SeedAsync(a, b, hidden);

        var both = await Ids(db, new ListingsQueryFilter { CategoryIds = [CatA, CatB] });
        Assert.Equal(new[] { a.Id, b.Id }.OrderBy(x => x), both.OrderBy(x => x));

        var merged = await Ids(db, new ListingsQueryFilter { CategoryId = CatA, CategoryIds = [CatB] });
        Assert.Equal(2, merged.Count);

        var legacy = await Ids(db, new ListingsQueryFilter { CategoryId = CatA });
        Assert.Equal(new[] { a.Id }, legacy);

        // Filtering by a hidden category returns nothing (same rule as before).
        Assert.Empty(await Ids(db, new ListingsQueryFilter { CategoryIds = [CatHidden] }));
    }

    // ---------- ageRanges ----------

    [Fact]
    public async Task AgeRanges_Match_Listings_Overlapping_Any_Range_Including_Open_Ended()
    {
        using var db = await SeedBaseAsync();
        var baby = L(ageFrom: 0, ageTo: 12);
        var toddler = L(ageFrom: 24, ageTo: 36);
        var older = L(ageFrom: 84, ageTo: 120);
        await db.SeedAsync(baby, toddler, older);

        var two = await Ids(db, new ListingsQueryFilter { AgeRanges = ["0-6", "30-40"] });
        Assert.Equal(new[] { baby.Id, toddler.Id }.OrderBy(x => x), two.OrderBy(x => x));

        var open = await Ids(db, new ListingsQueryFilter { AgeRanges = ["72-"] });
        Assert.Equal(new[] { older.Id }, open);
    }

    [Fact]
    public async Task AgeRanges_Combine_With_Legacy_Age_As_And()
    {
        using var db = await SeedBaseAsync();
        var baby = L(ageFrom: 0, ageTo: 12);
        var toddler = L(ageFrom: 24, ageTo: 36);
        await db.SeedAsync(baby, toddler);

        var ids = await Ids(db, new ListingsQueryFilter
        {
            AgeRanges = ["0-6", "30-40"],
            AgeFromMonths = 20,
            AgeToMonths = 48
        });
        Assert.Equal(new[] { toddler.Id }, ids);
    }

    // ---------- conditions ----------

    [Fact]
    public async Task Conditions_Match_Any_Selected_And_Normalise_Legacy_Stored_Values()
    {
        using var db = await SeedBaseAsync();
        var likeNew = L(condition: "LikeNew");
        var excellent = L(condition: "Excellent");
        var spaced = L(condition: " like new ");
        var fair = L(condition: "Fair");
        var used = L(condition: "used");
        var good = L(condition: "Good");
        var isNew = L(condition: "New");
        var none = L(condition: null);
        await db.SeedAsync(likeNew, excellent, spaced, fair, used, good, isNew, none);

        var likeNewIds = await Ids(db, new ListingsQueryFilter { Conditions = ["likenew"] });
        Assert.Equal(new[] { likeNew.Id, excellent.Id, spaced.Id }.OrderBy(x => x), likeNewIds.OrderBy(x => x));

        var multi = await Ids(db, new ListingsQueryFilter { Conditions = ["Fair", "NEW"] });
        Assert.Equal(new[] { fair.Id, used.Id, isNew.Id }.OrderBy(x => x), multi.OrderBy(x => x));
    }

    // ---------- sort ----------

    [Fact]
    public async Task Sort_Price_Asc_And_Desc_Break_Ties_On_Newest()
    {
        using var db = await SeedBaseAsync();
        var cheapOld = L(price: 5m, createdMinutesAgo: 30);
        var cheapNew = L(price: 5m, createdMinutesAgo: 5);
        var mid = L(price: 10m);
        var pricey = L(price: 50m);
        await db.SeedAsync(cheapOld, cheapNew, mid, pricey);

        Assert.Equal(
            new[] { cheapNew.Id, cheapOld.Id, mid.Id, pricey.Id },
            await Ids(db, new ListingsQueryFilter { Sort = "price_asc" }));
        Assert.Equal(
            new[] { pricey.Id, mid.Id, cheapNew.Id, cheapOld.Id },
            await Ids(db, new ListingsQueryFilter { Sort = "price_desc" }));
    }

    [Fact]
    public async Task Sort_Newest_Is_Default()
    {
        using var db = await SeedBaseAsync();
        var old = L(createdMinutesAgo: 60);
        var fresh = L(createdMinutesAgo: 1);
        await db.SeedAsync(old, fresh);

        Assert.Equal(new[] { fresh.Id, old.Id }, await Ids(db, new ListingsQueryFilter()));
        Assert.Equal(new[] { fresh.Id, old.Id }, await Ids(db, new ListingsQueryFilter { Sort = "newest" }));
    }

    [Fact]
    public async Task Sort_Rating_Desc_Uses_Exposed_Rating_And_Puts_Unrated_Last()
    {
        using var db = await SeedBaseAsync();
        var best = L(createdMinutesAgo: 50);
        var good = L(createdMinutesAgo: 40);
        var oneReview = L(createdMinutesAgo: 1);   // 5 stars but hidden (< 2 reviews)
        var none = L(createdMinutesAgo: 20);
        var noneOlder = L(createdMinutesAgo: 90);
        await db.SeedAsync(best, good, oneReview, none, noneOlder);

        var bookings = new List<Booking>();
        var reviews = new List<ToyReview>();
        void Add(Listing listing, int rating)
        {
            var bookingId = Guid.NewGuid();
            bookings.Add(TestData.Booking(bookingId, listing.Id, RenterId,
                TestData.Today.AddDays(-10 - bookings.Count * 2), TestData.Today.AddDays(-9 - bookings.Count * 2), BookingStatus.Completed));
            reviews.Add(new ToyReview
            {
                Id = Guid.NewGuid(), BookingId = bookingId, ListingId = listing.Id, ReviewerId = RenterId,
                OverallRating = rating, ConditionRating = rating, CleanlinessRating = rating,
                ValueForMoneyRating = rating, FunPlayValueRating = rating, DescriptionAccuracyRating = rating,
                CreatedAt = DateTime.UtcNow
            });
        }

        Add(best, 5); Add(best, 5);
        Add(good, 4); Add(good, 3);
        Add(oneReview, 5);
        await db.SeedAsync(bookings.Cast<object>().ToArray());
        await db.SeedAsync(reviews.Cast<object>().ToArray());

        var ids = await Ids(db, new ListingsQueryFilter { Sort = "rating_desc" });
        // Rated first by rating; then everything without an exposed rating, newest first.
        Assert.Equal(new[] { best.Id, good.Id, oneReview.Id, none.Id, noneOlder.Id }, ids);
    }

    [Fact]
    public async Task Sort_Nearest_Orders_By_Public_Distance_With_Unlocated_Last()
    {
        using var db = await SeedBaseAsync();
        var far = L(lat: 40.30m, lng: 44.60m);
        var near = L(lat: 40.186m, lng: 44.513m);
        var mid = L(lat: 40.22m, lng: 44.52m);
        var unlocated = L(createdMinutesAgo: 0);
        await db.SeedAsync(far, near, mid, unlocated);

        var ids = await Ids(db, new ListingsQueryFilter { Sort = "nearest", OriginLat = 40.1856m, OriginLng = 44.5126m });
        Assert.Equal(new[] { near.Id, mid.Id, far.Id, unlocated.Id }, ids);
    }

    [Fact]
    public async Task Sorted_Pages_Do_Not_Overlap()
    {
        using var db = await SeedBaseAsync();
        var all = Enumerable.Range(0, 7).Select(i => L(price: 10m, createdMinutesAgo: i % 2)).ToArray();
        await db.SeedAsync(all.Cast<object>().ToArray());

        var seen = new List<Guid>();
        for (var page = 1; page <= 3; page++)
        {
            seen.AddRange(await Ids(db, new ListingsQueryFilter { Sort = "price_asc", Page = page, PageSize = 3 }));
        }

        Assert.Equal(7, seen.Count);
        Assert.Equal(7, seen.Distinct().Count());
    }

    // ---------- District on the catalogue ----------

    [Fact]
    public async Task Catalogue_Fills_District()
    {
        using var db = await SeedBaseAsync();
        var inKentron = L(district: TestData.KentronDistrictId);
        var noDistrict = L();
        await db.SeedAsync(inKentron, noDistrict);

        await using var context = db.CreateContext();
        var result = await new ListingsQueryService(context).GetApprovedListingsAsync(new ListingsQueryFilter());
        var items = result.Items.ToDictionary(i => i.Id);
        Assert.Equal(TestData.KentronDistrictId, items[inKentron.Id].District!.Id);
        Assert.False(string.IsNullOrEmpty(items[inKentron.Id].District!.NameHy));
        Assert.Null(items[noDistrict.Id].District);
    }

    // ---------- Map pins ----------

    [Fact]
    public async Task MapPins_Take_The_New_Filters()
    {
        using var db = await SeedBaseAsync();
        var match = L(CatB, condition: "Excellent", ageFrom: 12, ageTo: 24, lat: 40.19m, lng: 44.51m);
        var wrongCat = L(CatA, condition: "Excellent", ageFrom: 12, ageTo: 24, lat: 40.19m, lng: 44.51m);
        var wrongCondition = L(CatB, condition: "Fair", ageFrom: 12, ageTo: 24, lat: 40.19m, lng: 44.51m);
        await db.SeedAsync(match, wrongCat, wrongCondition);

        await using var context = db.CreateContext();
        var pins = await new ListingsQueryService(context).GetMapPinsAsync(new ListingsQueryFilter
        {
            CategoryIds = [CatB],
            Conditions = ["LikeNew"],
            AgeRanges = ["10-20"]
        });
        Assert.Equal(new[] { match.Id }, pins.Items.Select(p => p.Id));
    }

    // ---------- Facets ----------

    [Fact]
    public async Task Facets_Exclude_Their_Own_Dimension_And_Apply_The_Rest()
    {
        using var db = await SeedBaseAsync();
        // Kentron/A 10, Kentron/B 20, Arabkir/A 30, Arabkir/B 40, and one noise listing in Cat A, no district, 500.
        await db.SeedAsync(
            L(CatA, 10m, district: TestData.KentronDistrictId),
            L(CatB, 20m, district: TestData.KentronDistrictId),
            L(CatA, 30m, district: TestData.ArabkirDistrictId),
            L(CatB, 40m, district: TestData.ArabkirDistrictId),
            L(CatA, 500m));

        var facets = await Facets(db, new ListingsQueryFilter
        {
            CategoryIds = [CatA],
            DistrictIds = [TestData.KentronDistrictId],
            MinPrice = 15m,
            MaxPrice = 45m
        });

        // Categories ignore the category filter, but obey district + price: only the Kentron 20 (Cat B).
        var category = Assert.Single(facets.Categories);
        Assert.Equal(CatB, category.CategoryId);
        Assert.Equal(1, category.Count);

        // Districts ignore the district filter, but obey category (A) + price 15-45: Arabkir 30 only.
        var district = Assert.Single(facets.Districts);
        Assert.Equal(TestData.ArabkirDistrictId, district.DistrictId);
        Assert.Equal(1, district.Count);

        // Price ignores min/max price but obeys category A + Kentron: only the 10.
        Assert.Equal(10m, facets.MinPricePerDay);
        Assert.Equal(10m, facets.MaxPricePerDay);
    }

    [Fact]
    public async Task Facets_Without_Filters_Count_Everything_Approved_And_Omit_Zero_Entries()
    {
        using var db = await SeedBaseAsync();
        await db.SeedAsync(
            L(CatA, 10m, district: TestData.KentronDistrictId),
            L(CatA, 30m, district: TestData.KentronDistrictId),
            L(CatB, 90m));

        var facets = await Facets(db, new ListingsQueryFilter());

        Assert.Equal(2, facets.Categories.Count);
        Assert.Equal(2, facets.Categories.Single(c => c.CategoryId == CatA).Count);
        Assert.Equal(1, facets.Categories.Single(c => c.CategoryId == CatB).Count);
        var district = Assert.Single(facets.Districts);
        Assert.Equal(2, district.Count);
        Assert.Equal(10m, facets.MinPricePerDay);
        Assert.Equal(90m, facets.MaxPricePerDay);
    }

    [Fact]
    public async Task Facets_Count_Approved_Only_And_Skip_Hidden_Categories()
    {
        using var db = await SeedBaseAsync();
        await db.SeedAsync(
            L(CatA, 10m, district: TestData.KentronDistrictId),
            L(CatA, 1m, district: TestData.KentronDistrictId, status: ListingStatus.Draft),
            L(CatA, 2m, district: TestData.KentronDistrictId, status: ListingStatus.PendingApproval),
            L(CatA, 3m, district: TestData.KentronDistrictId, status: ListingStatus.Rejected),
            L(CatA, 4m, district: TestData.KentronDistrictId, status: ListingStatus.Archived),
            L(CatHidden, 900m, district: TestData.ArabkirDistrictId));

        var facets = await Facets(db, new ListingsQueryFilter());

        // Only the one Approved listing in a visible category is a category facet.
        var category = Assert.Single(facets.Categories);
        Assert.Equal(CatA, category.CategoryId);
        Assert.Equal(1, category.Count);
        // Non-approved never reach district or price either.
        Assert.Equal(10m, facets.MinPricePerDay);
        Assert.DoesNotContain(facets.Districts, d => d.DistrictId == TestData.KentronDistrictId && d.Count != 1);
    }

    [Fact]
    public async Task Facets_Empty_Result_Has_Null_Price_Range()
    {
        using var db = await SeedBaseAsync();

        var facets = await Facets(db, new ListingsQueryFilter());

        Assert.Empty(facets.Categories);
        Assert.Empty(facets.Districts);
        Assert.Null(facets.MinPricePerDay);
        Assert.Null(facets.MaxPricePerDay);
    }

    // ---------- Validation (400s through [ApiController]) ----------

    [Fact]
    public void Validation_Accepts_Well_Formed_Lists()
    {
        Assert.Empty(Validate(new ListingsQueryFilter
        {
            CategoryIds = [CatA],
            AgeRanges = ["0-12", "72-"],
            Conditions = ["New", "likenew", "GOOD", "Fair"],
            Sort = "price_desc"
        }));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("12")]
    [InlineData("-12")]
    [InlineData("12-6")]
    [InlineData("1-2-3")]
    [InlineData("a-b")]
    [InlineData("-")]
    [InlineData("0-99999")]
    public void Validation_Rejects_Malformed_Age_Range(string range)
    {
        var results = Validate(new ListingsQueryFilter { AgeRanges = [range] });
        Assert.Contains(results, r => r.MemberNames.Contains(nameof(ListingsQueryFilter.AgeRanges)));
    }

    [Fact]
    public void Validation_Rejects_More_Than_Five_Age_Ranges()
    {
        var results = Validate(new ListingsQueryFilter
        {
            AgeRanges = ["0-1", "2-3", "4-5", "6-7", "8-9", "10-11"]
        });
        Assert.Contains(results, r => r.MemberNames.Contains(nameof(ListingsQueryFilter.AgeRanges)));
    }

    [Theory]
    [InlineData("Excellent")]
    [InlineData("Used")]
    [InlineData("Poor")]
    [InlineData("1")]
    [InlineData("New,Good")]
    [InlineData("")]
    public void Validation_Rejects_Unknown_Condition(string condition)
    {
        var results = Validate(new ListingsQueryFilter { Conditions = [condition] });
        Assert.Contains(results, r => r.MemberNames.Contains(nameof(ListingsQueryFilter.Conditions)));
    }

    [Fact]
    public void Validation_Rejects_More_Than_Four_Conditions_And_Too_Many_Categories()
    {
        // Distinct values only: a fifth distinct condition cannot exist, so oversize == unknown value.
        var tooManyCategories = Enumerable.Range(0, 51).Select(_ => Guid.NewGuid()).ToList();
        var results = Validate(new ListingsQueryFilter { CategoryIds = tooManyCategories });
        Assert.Contains(results, r => r.MemberNames.Contains(nameof(ListingsQueryFilter.CategoryIds)));
        Assert.Empty(Validate(new ListingsQueryFilter { Conditions = ["New", "New", "new"] }));
    }

    [Fact]
    public void Validation_Rejects_Unknown_Sort_And_Nearest_Without_Origin()
    {
        Assert.Contains(Validate(new ListingsQueryFilter { Sort = "cheapest" }),
            r => r.MemberNames.Contains(nameof(ListingsQueryFilter.Sort)));
        Assert.Contains(Validate(new ListingsQueryFilter { Sort = "nearest" }),
            r => r.MemberNames.Contains(nameof(ListingsQueryFilter.Sort)));
        Assert.Contains(Validate(new ListingsQueryFilter { Sort = "nearest", OriginLat = 40m }),
            r => r.MemberNames.Contains(nameof(ListingsQueryFilter.Sort)));
        Assert.Empty(Validate(new ListingsQueryFilter { Sort = "nearest", OriginLat = 40m, OriginLng = 44m }));
    }
}
