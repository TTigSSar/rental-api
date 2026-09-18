using RentalPlatform.Application.DTOs;
using RentalPlatform.Application.Services;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Infrastructure.Persistence;
using RentalPlatform.Infrastructure.Services;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Listings;

// Owner-side listing lifecycle: create validation, edit re-moderation, archive/restore.
// Runs the real ListingsOwnerService over the real ListingsOwnerStore against SQLite.
public sealed class ListingsOwnerServiceTests
{
    private static readonly Guid OwnerId = new("c0000000-0000-0000-0000-000000000001");
    private static readonly Guid CategoryId = new("c0000000-0000-0000-0000-000000000002");
    private static readonly Guid ListingId = new("c0000000-0000-0000-0000-000000000003");

    private static async Task SeedBaselineAsync(SqliteTestDatabase db, bool ownerBlocked = false)
    {
        await db.SeedAsync(
            TestData.User(OwnerId, "owner@test.local", isBlocked: ownerBlocked),
            TestData.Category(CategoryId));
    }

    private static ListingsOwnerService CreateService(AppDbContext context, Guid currentUserId) =>
        new(
            new FakeCurrentUserContext(currentUserId),
            new ListingsOwnerStore(context),
            new GeohashSnapper(),
            new DistrictBoundaryProvider());

    private static CreateListingRequest ValidCreate(
        Guid? categoryId = null,
        int? ageFromMonths = null,
        int? ageToMonths = null,
        PriceUnit? priceUnit = null,
        int? minRentalDays = null,
        DeliveryType? deliveryType = null,
        IReadOnlyList<DeliveryType>? deliveryTypes = null) => new()
    {
        CategoryId = categoryId ?? CategoryId,
        Title = "Wooden Train Set",
        Description = "A long enough description to satisfy validation rules.",
        PricePerDay = 12m,
        PriceUnit = priceUnit,
        Country = "Armenia",
        City = "Yerevan",
        AgeFromMonths = ageFromMonths,
        AgeToMonths = ageToMonths,
        MinRentalDays = minRentalDays,
        DeliveryType = deliveryType,
        DeliveryTypes = deliveryTypes
    };

    private static async Task SeedApprovedListingAsync(SqliteTestDatabase db)
    {
        await db.SeedAsync(TestData.Listing(ListingId, OwnerId, CategoryId, ListingStatus.Approved));
    }

    [Fact]
    public async Task Create_Submits_Listing_For_Review()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);

        await using var context = db.CreateContext();
        var result = await CreateService(context, OwnerId).CreateAsync(ValidCreate());

        Assert.True(result.IsSuccess);
        Assert.Equal(ListingStatus.PendingApproval, result.Value!.Status);
    }

    [Fact]
    public async Task Create_Defaults_PriceUnit_To_Daily_When_Omitted()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);

        await using var context = db.CreateContext();
        var result = await CreateService(context, OwnerId).CreateAsync(ValidCreate(priceUnit: null));

        Assert.True(result.IsSuccess);

        await using var verify = db.CreateContext();
        var stored = await verify.Listings.FindAsync(result.Value!.Id);
        Assert.Equal(PriceUnit.Daily, stored!.PriceUnit);
    }

    [Fact]
    public async Task Create_Persists_Explicit_PriceUnit()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);

        await using var context = db.CreateContext();
        // Hourly is the CLR default (0); persisting it proves the configured store default does not
        // silently overwrite an explicit choice.
        var result = await CreateService(context, OwnerId).CreateAsync(ValidCreate(priceUnit: PriceUnit.Hourly));

        Assert.True(result.IsSuccess);

        await using var verify = db.CreateContext();
        var stored = await verify.Listings.FindAsync(result.Value!.Id);
        Assert.Equal(PriceUnit.Hourly, stored!.PriceUnit);
    }

    [Fact]
    public async Task Update_Changes_PriceUnit_Without_Re_Moderation()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);
        await SeedApprovedListingAsync(db);

        await using var context = db.CreateContext();
        var result = await CreateService(context, OwnerId).UpdateAsync(ListingId, new UpdateListingRequest
        {
            PriceUnit = PriceUnit.Weekly
        });

        Assert.True(result.IsSuccess);

        await using var verify = db.CreateContext();
        var stored = await verify.Listings.FindAsync(ListingId);
        // Price/unit are structured fields, so an approved listing stays approved.
        Assert.Equal(ListingStatus.Approved, stored!.Status);
        Assert.Equal(PriceUnit.Weekly, stored.PriceUnit);
    }

    [Fact]
    public async Task Update_Leaves_PriceUnit_Unchanged_When_Omitted()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);
        // TestData.Listing leaves PriceUnit at the entity default (Daily); the migration's store
        // default backfills the column to the same value for existing rows.
        await SeedApprovedListingAsync(db);

        await using var context = db.CreateContext();
        var result = await CreateService(context, OwnerId).UpdateAsync(ListingId, new UpdateListingRequest
        {
            PricePerDay = 50m
        });

        Assert.True(result.IsSuccess);

        await using var verify = db.CreateContext();
        var stored = await verify.Listings.FindAsync(ListingId);
        Assert.Equal(PriceUnit.Daily, stored!.PriceUnit);
    }

    [Fact]
    public async Task Update_Leaves_CompensationAmount_Unchanged_When_Omitted()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);
        // TestData.Listing seeds CompensationAmount = 25m.
        await SeedApprovedListingAsync(db);

        await using var context = db.CreateContext();
        var result = await CreateService(context, OwnerId).UpdateAsync(ListingId, new UpdateListingRequest
        {
            PricePerDay = 50m
        });

        Assert.True(result.IsSuccess);

        await using var verify = db.CreateContext();
        var stored = await verify.Listings.FindAsync(ListingId);
        Assert.Equal(25m, stored!.CompensationAmount);
    }

    [Fact]
    public async Task Update_Changes_CompensationAmount_Without_Re_Moderation()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);
        await SeedApprovedListingAsync(db);

        await using var context = db.CreateContext();
        var result = await CreateService(context, OwnerId).UpdateAsync(ListingId, new UpdateListingRequest
        {
            CompensationAmount = 40000m
        });

        Assert.True(result.IsSuccess);

        await using var verify = db.CreateContext();
        var stored = await verify.Listings.FindAsync(ListingId);
        // CompensationAmount is a structured field, so an approved listing stays approved.
        Assert.Equal(ListingStatus.Approved, stored!.Status);
        Assert.Equal(40000m, stored.CompensationAmount);
    }

    [Fact]
    public async Task Update_With_Null_DeliveryTypes_And_DeliveryType_Leaves_Delivery_Unchanged()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);
        var listing = TestData.Listing(ListingId, OwnerId, CategoryId, ListingStatus.Approved);
        listing.DeliveryOptions = DeliveryOptions.Pickup | DeliveryOptions.Courier;
        listing.DeliveryType = DeliveryType.Pickup;
        await db.SeedAsync(listing);

        await using var context = db.CreateContext();
        // Neither DeliveryTypes nor DeliveryType supplied — delivery is a structured field, so
        // omitting both must leave the stored value untouched (same pattern as PriceUnit above),
        // and must not trigger re-moderation (the listing stays Approved).
        var result = await CreateService(context, OwnerId).UpdateAsync(ListingId, new UpdateListingRequest
        {
            PricePerDay = 15m
        });

        Assert.True(result.IsSuccess);

        await using var verify = db.CreateContext();
        var stored = await verify.Listings.FindAsync(ListingId);
        Assert.Equal(ListingStatus.Approved, stored!.Status);
        Assert.Equal(DeliveryOptions.Pickup | DeliveryOptions.Courier, stored.DeliveryOptions);
        Assert.Equal(DeliveryType.Pickup, stored.DeliveryType);
    }

    // Regression coverage for the stale-client bug: an update that supplies ONLY the legacy scalar
    // DeliveryType (DeliveryTypes omitted) must not blindly collapse a richer multi-select
    // DeliveryOptions down to that single flag when the flag is already included in the current
    // state. See DeliveryOptionsMapper.ShouldApplyLegacyOnlyUpdate.
    [Fact]
    public async Task Update_With_LegacyOnly_Pickup_On_PickupAndCourier_Listing_Is_NoOp()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);
        var listing = TestData.Listing(ListingId, OwnerId, CategoryId, ListingStatus.Approved);
        listing.DeliveryOptions = DeliveryOptions.Pickup | DeliveryOptions.Courier;
        listing.DeliveryType = DeliveryType.Pickup;
        await db.SeedAsync(listing);

        await using var context = db.CreateContext();
        // Legacy-only Pickup — a stale client whose form always sends its old default — is
        // consistent with the current flags (Pickup is already included), so it's a no-op.
        var result = await CreateService(context, OwnerId).UpdateAsync(ListingId, new UpdateListingRequest
        {
            DeliveryType = DeliveryType.Pickup
        });

        Assert.True(result.IsSuccess);

        await using var verify = db.CreateContext();
        var stored = await verify.Listings.FindAsync(ListingId);
        Assert.Equal(DeliveryOptions.Pickup | DeliveryOptions.Courier, stored!.DeliveryOptions);
        Assert.Equal(DeliveryType.Pickup, stored.DeliveryType);
    }

    [Fact]
    public async Task Update_With_LegacyOnly_Courier_On_PickupAndCourier_Listing_Is_NoOp()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);
        var listing = TestData.Listing(ListingId, OwnerId, CategoryId, ListingStatus.Approved);
        listing.DeliveryOptions = DeliveryOptions.Pickup | DeliveryOptions.Courier;
        listing.DeliveryType = DeliveryType.Pickup;
        await db.SeedAsync(listing);

        await using var context = db.CreateContext();
        // Legacy-only Courier is also already included in the current flags, so this is a no-op
        // too — the listing stays Pickup|Courier with legacy mirror unchanged at Pickup. This is
        // the exact scenario from the bug report: saving any edit must not silently drop Courier.
        var result = await CreateService(context, OwnerId).UpdateAsync(ListingId, new UpdateListingRequest
        {
            DeliveryType = DeliveryType.Courier
        });

        Assert.True(result.IsSuccess);

        await using var verify = db.CreateContext();
        var stored = await verify.Listings.FindAsync(ListingId);
        Assert.Equal(DeliveryOptions.Pickup | DeliveryOptions.Courier, stored!.DeliveryOptions);
        Assert.Equal(DeliveryType.Pickup, stored.DeliveryType);
    }

    [Fact]
    public async Task Update_With_LegacyOnly_Pickup_On_CourierOnly_Listing_Becomes_Pickup()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);
        var listing = TestData.Listing(ListingId, OwnerId, CategoryId, ListingStatus.Approved);
        listing.DeliveryOptions = DeliveryOptions.Courier;
        listing.DeliveryType = DeliveryType.Courier;
        await db.SeedAsync(listing);

        await using var context = db.CreateContext();
        // Legacy-only Pickup is a real change from the current Courier-only state — this preserves
        // old-client semantics: collapse to the single reported flag, same as pre-fix behaviour.
        var result = await CreateService(context, OwnerId).UpdateAsync(ListingId, new UpdateListingRequest
        {
            DeliveryType = DeliveryType.Pickup
        });

        Assert.True(result.IsSuccess);

        await using var verify = db.CreateContext();
        var stored = await verify.Listings.FindAsync(ListingId);
        Assert.Equal(DeliveryOptions.Pickup, stored!.DeliveryOptions);
        Assert.Equal(DeliveryType.Pickup, stored.DeliveryType);
    }

    [Fact]
    public async Task Update_With_LegacyOnly_Courier_On_Listing_With_Null_DeliveryOptions_Becomes_Courier()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);
        var listing = TestData.Listing(ListingId, OwnerId, CategoryId, ListingStatus.Approved);
        listing.DeliveryOptions = null;
        listing.DeliveryType = null;
        await db.SeedAsync(listing);

        await using var context = db.CreateContext();
        // Null current DeliveryOptions (pre-migration row) — behaves as today: sets the single flag.
        var result = await CreateService(context, OwnerId).UpdateAsync(ListingId, new UpdateListingRequest
        {
            DeliveryType = DeliveryType.Courier
        });

        Assert.True(result.IsSuccess);

        await using var verify = db.CreateContext();
        var stored = await verify.Listings.FindAsync(ListingId);
        Assert.Equal(DeliveryOptions.Courier, stored!.DeliveryOptions);
        Assert.Equal(DeliveryType.Courier, stored.DeliveryType);
    }

    [Fact]
    public async Task Create_Fails_When_Category_Missing()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);

        var request = ValidCreate(categoryId: Guid.NewGuid());

        await using var context = db.CreateContext();
        var result = await CreateService(context, OwnerId).CreateAsync(request);

        Assert.False(result.IsSuccess);
        Assert.Equal("listing.category_not_found", result.Error!.Code);
    }

    [Fact]
    public async Task Create_Fails_When_Age_Range_Inverted()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);

        var request = ValidCreate(ageFromMonths: 24, ageToMonths: 12);

        await using var context = db.CreateContext();
        var result = await CreateService(context, OwnerId).CreateAsync(request);

        Assert.False(result.IsSuccess);
        Assert.Equal("listing.invalid_age_range", result.Error!.Code);
    }

    [Fact]
    public async Task Create_Fails_When_Owner_Blocked()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db, ownerBlocked: true);

        await using var context = db.CreateContext();
        var result = await CreateService(context, OwnerId).CreateAsync(ValidCreate());

        Assert.False(result.IsSuccess);
        Assert.Equal("listing.user_blocked", result.Error!.Code);
    }

    // DeliveryTypes is the additive multi-select successor to the legacy scalar DeliveryType (see
    // DeliveryOptionsMapper). GetMineAsync's projection is exercised here alongside CreateAsync
    // since MyListingResponse is the only place both the expanded list and the legacy mirror are
    // observable together.
    [Fact]
    public async Task Create_With_Multiple_DeliveryTypes_Returns_Both_And_Mirrors_Legacy_Pickup()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);

        await using var context = db.CreateContext();
        var service = CreateService(context, OwnerId);
        var created = await service.CreateAsync(ValidCreate(deliveryTypes: new[] { DeliveryType.Pickup, DeliveryType.Courier }));
        Assert.True(created.IsSuccess);

        var mine = await service.GetMineAsync();
        Assert.True(mine.IsSuccess);
        var listing = Assert.Single(mine.Value!, l => l.Id == created.Value!.Id);
        Assert.Equal(new[] { DeliveryType.Pickup, DeliveryType.Courier }, listing.DeliveryTypes);
        Assert.Equal(DeliveryType.Pickup, listing.DeliveryType);
    }

    [Fact]
    public async Task Create_With_Courier_Only_Mirrors_Legacy_Courier()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);

        await using var context = db.CreateContext();
        var service = CreateService(context, OwnerId);
        var created = await service.CreateAsync(ValidCreate(deliveryTypes: new[] { DeliveryType.Courier }));
        Assert.True(created.IsSuccess);

        var mine = await service.GetMineAsync();
        var listing = Assert.Single(mine.Value!, l => l.Id == created.Value!.Id);
        Assert.Equal(new[] { DeliveryType.Courier }, listing.DeliveryTypes);
        Assert.Equal(DeliveryType.Courier, listing.DeliveryType);
    }

    [Fact]
    public async Task Create_With_Legacy_DeliveryType_Only_Populates_DeliveryTypes_List()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);

        await using var context = db.CreateContext();
        var service = CreateService(context, OwnerId);
        var created = await service.CreateAsync(ValidCreate(deliveryType: DeliveryType.Courier));
        Assert.True(created.IsSuccess);

        var mine = await service.GetMineAsync();
        var listing = Assert.Single(mine.Value!, l => l.Id == created.Value!.Id);
        Assert.Equal(new[] { DeliveryType.Courier }, listing.DeliveryTypes);
        Assert.Equal(DeliveryType.Courier, listing.DeliveryType);
    }

    [Fact]
    public async Task Create_Accepts_MinRentalDays_365()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);

        await using var context = db.CreateContext();
        var result = await CreateService(context, OwnerId).CreateAsync(ValidCreate(minRentalDays: 365));

        Assert.True(result.IsSuccess);

        await using var verify = db.CreateContext();
        var stored = await verify.Listings.FindAsync(result.Value!.Id);
        Assert.Equal(365, stored!.MinRentalDays);
    }

    [Fact]
    public async Task Update_Resets_Approved_Listing_To_Pending_When_Content_Changes()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);
        await SeedApprovedListingAsync(db);

        await using var context = db.CreateContext();
        var result = await CreateService(context, OwnerId).UpdateAsync(ListingId, new UpdateListingRequest
        {
            Description = "An edited description that changes the public listing content."
        });

        Assert.True(result.IsSuccess);

        await using var verify = db.CreateContext();
        var stored = await verify.Listings.FindAsync(ListingId);
        Assert.Equal(ListingStatus.PendingApproval, stored!.Status);
    }

    [Fact]
    public async Task Update_Keeps_Approved_Listing_Approved_When_Only_Price_Changes()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);
        await SeedApprovedListingAsync(db);

        await using var context = db.CreateContext();
        var result = await CreateService(context, OwnerId).UpdateAsync(ListingId, new UpdateListingRequest
        {
            PricePerDay = 99m
        });

        Assert.True(result.IsSuccess);

        await using var verify = db.CreateContext();
        var stored = await verify.Listings.FindAsync(ListingId);
        Assert.Equal(ListingStatus.Approved, stored!.Status);
        Assert.Equal(99m, stored.PricePerDay);
    }

    [Fact]
    public async Task Update_Resends_Rejected_Listing_For_Review_And_Clears_Reason()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);
        var listing = TestData.Listing(ListingId, OwnerId, CategoryId, ListingStatus.Rejected);
        listing.RejectionReason = "Unsafe parts.";
        await db.SeedAsync(listing);

        await using var context = db.CreateContext();
        var result = await CreateService(context, OwnerId).UpdateAsync(ListingId, new UpdateListingRequest
        {
            Title = "Refreshed Title"
        });

        Assert.True(result.IsSuccess);

        await using var verify = db.CreateContext();
        var stored = await verify.Listings.FindAsync(ListingId);
        Assert.Equal(ListingStatus.PendingApproval, stored!.Status);
        Assert.Null(stored.RejectionReason);
    }

    [Fact]
    public async Task Update_Fails_On_Archived_Listing()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);
        await db.SeedAsync(TestData.Listing(ListingId, OwnerId, CategoryId, ListingStatus.Archived));

        await using var context = db.CreateContext();
        var result = await CreateService(context, OwnerId).UpdateAsync(ListingId, new UpdateListingRequest
        {
            Title = "Cannot edit archived"
        });

        Assert.False(result.IsSuccess);
        Assert.Equal("listing.invalid_status", result.Error!.Code);
    }

    [Fact]
    public async Task Resubmit_Moves_Rejected_Listing_Back_To_Pending_And_Clears_Reason()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);
        var listing = TestData.Listing(ListingId, OwnerId, CategoryId, ListingStatus.Rejected);
        listing.RejectionReason = "Unsafe parts.";
        listing.RejectionReasonCode = "safety";
        listing.RejectionNote = "Small detachable wheels.";
        await db.SeedAsync(listing);

        await using var context = db.CreateContext();
        var result = await CreateService(context, OwnerId).ResubmitAsync(ListingId);

        Assert.True(result.IsSuccess);

        await using var verify = db.CreateContext();
        var stored = await verify.Listings.FindAsync(ListingId);
        Assert.Equal(ListingStatus.PendingApproval, stored!.Status);
        Assert.Null(stored.RejectionReason);
        Assert.Null(stored.RejectionReasonCode);
        Assert.Null(stored.RejectionNote);
    }

    [Fact]
    public async Task Resubmit_Is_Idempotent_When_Already_Pending()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);
        await db.SeedAsync(TestData.Listing(ListingId, OwnerId, CategoryId, ListingStatus.PendingApproval));

        await using var context = db.CreateContext();
        var result = await CreateService(context, OwnerId).ResubmitAsync(ListingId);

        Assert.True(result.IsSuccess);

        await using var verify = db.CreateContext();
        var stored = await verify.Listings.FindAsync(ListingId);
        Assert.Equal(ListingStatus.PendingApproval, stored!.Status);
    }

    [Fact]
    public async Task Resubmit_Fails_On_Approved_Listing()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);
        await SeedApprovedListingAsync(db);

        await using var context = db.CreateContext();
        var result = await CreateService(context, OwnerId).ResubmitAsync(ListingId);

        Assert.False(result.IsSuccess);
        Assert.Equal("listing.invalid_status", result.Error!.Code);
    }

    [Fact]
    public async Task Resubmit_Fails_When_Listing_Not_Found()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);

        await using var context = db.CreateContext();
        var result = await CreateService(context, OwnerId).ResubmitAsync(Guid.NewGuid());

        Assert.False(result.IsSuccess);
        Assert.Equal("listing.not_found", result.Error!.Code);
    }

    [Fact]
    public async Task Archive_Then_Archive_Again_Fails()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);
        await SeedApprovedListingAsync(db);

        await using (var context = db.CreateContext())
        {
            var first = await CreateService(context, OwnerId).ArchiveAsync(ListingId);
            Assert.True(first.IsSuccess);
        }

        await using var second = db.CreateContext();
        var result = await CreateService(second, OwnerId).ArchiveAsync(ListingId);

        Assert.False(result.IsSuccess);
        Assert.Equal("listing.invalid_status", result.Error!.Code);
    }

    [Fact]
    public async Task Restore_Moves_Archived_Listing_Back_To_Pending()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);
        await db.SeedAsync(TestData.Listing(ListingId, OwnerId, CategoryId, ListingStatus.Archived));

        await using var context = db.CreateContext();
        var result = await CreateService(context, OwnerId).RestoreAsync(ListingId);

        Assert.True(result.IsSuccess);

        await using var verify = db.CreateContext();
        var stored = await verify.Listings.FindAsync(ListingId);
        Assert.Equal(ListingStatus.PendingApproval, stored!.Status);
    }
}
