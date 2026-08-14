using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RentalPlatform.Application.DTOs;
using RentalPlatform.Application.Services;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Infrastructure.Persistence;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Users;

// Admin console Phase 3: the Users screen backend. Covers status derivation (Pending/Active/
// Suspended from IsBlocked/IsIdConfirmed — nothing persisted), search across all four name/email
// forms, every sort order, pagination bounds, the search-filtered (not status-filtered) summary
// counts, verify/suspend/reactivate idempotency and round-trip, both safety guards, and one
// moderation-log entry per mutation.
public sealed class AdminUsersServiceTests
{
    private static readonly Guid AdminId = new("a1000000-0000-0000-0000-000000000001");
    private static readonly Guid OtherAdminId = new("a1000000-0000-0000-0000-000000000002");
    private static readonly Guid CategoryId = new("a1000000-0000-0000-0000-000000000003");

    private static AdminUsersService CreateService(AppDbContext context, Guid currentUserId) =>
        new(
            new FakeCurrentUserContext(currentUserId),
            new AdminUsersStore(context),
            new ModerationLogStore(context, NullLogger<ModerationLogStore>.Instance));

    private static async Task SeedAdminAsync(SqliteTestDatabase db) =>
        await db.SeedAsync(TestData.User(AdminId, "admin@test.local", role: UserRole.Admin, isIdConfirmed: true));

    // ---------- Status derivation ----------

    [Fact]
    public async Task GetQueue_Derives_Pending_Active_Suspended_Status_Correctly()
    {
        using var db = new SqliteTestDatabase();
        await SeedAdminAsync(db);
        var pendingId = new Guid("a1000000-0000-0000-0000-000000000010");
        var activeId = new Guid("a1000000-0000-0000-0000-000000000011");
        var suspendedId = new Guid("a1000000-0000-0000-0000-000000000012");
        await db.SeedAsync(
            TestData.User(pendingId, "pending@test.local", isBlocked: false, isIdConfirmed: false),
            TestData.User(activeId, "active@test.local", isBlocked: false, isIdConfirmed: true),
            TestData.User(suspendedId, "suspended@test.local", isBlocked: true, isIdConfirmed: true));

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).GetQueueAsync(new AdminUserQueueFilter { Status = "all" });

        Assert.True(result.IsSuccess);
        var items = result.Value!.Items;
        Assert.Equal(UserAccountStatus.Pending, items.Single(i => i.Id == pendingId).Status);
        Assert.Equal(UserAccountStatus.Active, items.Single(i => i.Id == activeId).Status);
        Assert.Equal(UserAccountStatus.Suspended, items.Single(i => i.Id == suspendedId).Status);
    }

    [Fact]
    public async Task GetQueue_Suspended_Blocked_User_Is_Suspended_Even_If_IdConfirmed()
    {
        // Suspended must take priority over Pending/Active — a blocked, ID-confirmed user is still
        // Suspended, not Active.
        using var db = new SqliteTestDatabase();
        await SeedAdminAsync(db);
        var userId = new Guid("a1000000-0000-0000-0000-000000000013");
        await db.SeedAsync(TestData.User(userId, "blocked@test.local", isBlocked: true, isIdConfirmed: true));

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).GetQueueAsync(new AdminUserQueueFilter { Status = "all" });

        Assert.Equal(UserAccountStatus.Suspended, result.Value!.Items.Single(i => i.Id == userId).Status);
    }

    [Fact]
    public async Task GetQueue_Status_Filter_Pending_Excludes_Active_And_Suspended()
    {
        using var db = new SqliteTestDatabase();
        await SeedAdminAsync(db);
        var pendingId = new Guid("a1000000-0000-0000-0000-000000000014");
        var activeId = new Guid("a1000000-0000-0000-0000-000000000015");
        var suspendedId = new Guid("a1000000-0000-0000-0000-000000000016");
        await db.SeedAsync(
            TestData.User(pendingId, "pending2@test.local", isBlocked: false, isIdConfirmed: false),
            TestData.User(activeId, "active2@test.local", isBlocked: false, isIdConfirmed: true),
            TestData.User(suspendedId, "suspended2@test.local", isBlocked: true, isIdConfirmed: true));

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).GetQueueAsync(new AdminUserQueueFilter { Status = "pending" });

        var ids = result.Value!.Items.Select(i => i.Id).ToList();
        Assert.Contains(pendingId, ids);
        Assert.DoesNotContain(activeId, ids);
        Assert.DoesNotContain(suspendedId, ids);
    }

    [Fact]
    public async Task GetQueue_Status_Filter_Suspended_Excludes_Pending_And_Active()
    {
        using var db = new SqliteTestDatabase();
        await SeedAdminAsync(db);
        var pendingId = new Guid("a1000000-0000-0000-0000-000000000017");
        var suspendedId = new Guid("a1000000-0000-0000-0000-000000000018");
        await db.SeedAsync(
            TestData.User(pendingId, "pending3@test.local", isBlocked: false, isIdConfirmed: false),
            TestData.User(suspendedId, "suspended3@test.local", isBlocked: true, isIdConfirmed: true));

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).GetQueueAsync(new AdminUserQueueFilter { Status = "suspended" });

        var ids = result.Value!.Items.Select(i => i.Id).ToList();
        Assert.DoesNotContain(pendingId, ids);
        Assert.Contains(suspendedId, ids);
    }

    // ---------- Search across all four name/email forms ----------

    private static async Task SeedSearchTargetAsync(SqliteTestDatabase db, Guid id) =>
        await db.SeedAsync(TestData.User(id, "narek.owner@test.local", firstName: "Narek", lastName: "Owner"));

    [Fact]
    public async Task GetQueue_Search_Matches_FirstName()
    {
        using var db = new SqliteTestDatabase();
        await SeedAdminAsync(db);
        var targetId = new Guid("a1000000-0000-0000-0000-000000000020");
        await SeedSearchTargetAsync(db, targetId);

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).GetQueueAsync(new AdminUserQueueFilter { Search = "narek" });

        Assert.Contains(targetId, result.Value!.Items.Select(i => i.Id));
    }

    [Fact]
    public async Task GetQueue_Search_Matches_LastName()
    {
        using var db = new SqliteTestDatabase();
        await SeedAdminAsync(db);
        var targetId = new Guid("a1000000-0000-0000-0000-000000000021");
        await SeedSearchTargetAsync(db, targetId);

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).GetQueueAsync(new AdminUserQueueFilter { Search = "owner" });

        Assert.Contains(targetId, result.Value!.Items.Select(i => i.Id));
    }

    [Fact]
    public async Task GetQueue_Search_Matches_FullName()
    {
        using var db = new SqliteTestDatabase();
        await SeedAdminAsync(db);
        var targetId = new Guid("a1000000-0000-0000-0000-000000000022");
        await SeedSearchTargetAsync(db, targetId);

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).GetQueueAsync(new AdminUserQueueFilter { Search = "Narek Owner" });

        Assert.Contains(targetId, result.Value!.Items.Select(i => i.Id));
    }

    [Fact]
    public async Task GetQueue_Search_Matches_Email()
    {
        using var db = new SqliteTestDatabase();
        await SeedAdminAsync(db);
        var targetId = new Guid("a1000000-0000-0000-0000-000000000023");
        await SeedSearchTargetAsync(db, targetId);

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).GetQueueAsync(new AdminUserQueueFilter { Search = "narek.owner@test.local" });

        Assert.Contains(targetId, result.Value!.Items.Select(i => i.Id));
    }

    [Fact]
    public async Task GetQueue_Search_Is_Case_Insensitive()
    {
        using var db = new SqliteTestDatabase();
        await SeedAdminAsync(db);
        var targetId = new Guid("a1000000-0000-0000-0000-000000000024");
        await SeedSearchTargetAsync(db, targetId);

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).GetQueueAsync(new AdminUserQueueFilter { Search = "NAREK" });

        Assert.Contains(targetId, result.Value!.Items.Select(i => i.Id));
    }

    // ---------- Sort orders ----------

    [Fact]
    public async Task GetQueue_Sort_Name_Orders_Alphabetically_By_First_Then_Last()
    {
        using var db = new SqliteTestDatabase();
        await SeedAdminAsync(db);
        var bId = new Guid("a1000000-0000-0000-0000-000000000030");
        var aId = new Guid("a1000000-0000-0000-0000-000000000031");
        await db.SeedAsync(
            TestData.User(bId, "b@test.local", firstName: "Bella", lastName: "Zed"),
            TestData.User(aId, "a@test.local", firstName: "Anna", lastName: "Aaa"));

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).GetQueueAsync(new AdminUserQueueFilter { Status = "all", Sort = "name" });

        var ids = result.Value!.Items.Select(i => i.Id).Where(id => id == aId || id == bId).ToList();
        Assert.Equal([aId, bId], ids);
    }

    [Fact]
    public async Task GetQueue_Sort_Newest_Orders_By_CreatedAt_Descending()
    {
        using var db = new SqliteTestDatabase();
        await SeedAdminAsync(db);
        var olderId = new Guid("a1000000-0000-0000-0000-000000000032");
        var newerId = new Guid("a1000000-0000-0000-0000-000000000033");
        await db.SeedAsync(
            TestData.User(olderId, "older@test.local", createdAt: DateTime.UtcNow.AddDays(-5)),
            TestData.User(newerId, "newer@test.local", createdAt: DateTime.UtcNow.AddDays(-1)));

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).GetQueueAsync(new AdminUserQueueFilter { Status = "all", Sort = "newest" });

        var ids = result.Value!.Items.Select(i => i.Id).Where(id => id == olderId || id == newerId).ToList();
        Assert.Equal([newerId, olderId], ids);
    }

    [Fact]
    public async Task GetQueue_Sort_Listings_Orders_By_Approved_Listing_Count_Descending()
    {
        using var db = new SqliteTestDatabase();
        await SeedAdminAsync(db);
        await db.SeedAsync(TestData.Category(CategoryId));
        var oneListingId = new Guid("a1000000-0000-0000-0000-000000000034");
        var twoListingsId = new Guid("a1000000-0000-0000-0000-000000000035");
        await db.SeedAsync(
            TestData.User(oneListingId, "one@test.local"),
            TestData.User(twoListingsId, "two@test.local"));
        await db.SeedAsync(
            TestData.Listing(Guid.NewGuid(), oneListingId, CategoryId, ListingStatus.Approved),
            TestData.Listing(Guid.NewGuid(), twoListingsId, CategoryId, ListingStatus.Approved),
            TestData.Listing(Guid.NewGuid(), twoListingsId, CategoryId, ListingStatus.Approved),
            // A Pending listing must not count towards ListingCount/the sort.
            TestData.Listing(Guid.NewGuid(), oneListingId, CategoryId, ListingStatus.PendingApproval));

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).GetQueueAsync(new AdminUserQueueFilter { Status = "all", Sort = "listings" });

        var ids = result.Value!.Items.Select(i => i.Id).Where(id => id == oneListingId || id == twoListingsId).ToList();
        Assert.Equal([twoListingsId, oneListingId], ids);
        Assert.Equal(2, result.Value.Items.Single(i => i.Id == twoListingsId).ListingCount);
        Assert.Equal(1, result.Value.Items.Single(i => i.Id == oneListingId).ListingCount);
    }

    [Fact]
    public async Task GetQueue_Sort_Rentals_Orders_By_Completed_Rental_Count_Descending()
    {
        using var db = new SqliteTestDatabase();
        await SeedAdminAsync(db);
        var ownerId = new Guid("a1000000-0000-0000-0000-000000000036");
        var oneRentalId = new Guid("a1000000-0000-0000-0000-000000000037");
        var twoRentalsId = new Guid("a1000000-0000-0000-0000-000000000038");
        await db.SeedAsync(
            TestData.Category(CategoryId),
            TestData.User(ownerId, "owner4@test.local"),
            TestData.User(oneRentalId, "one-rental@test.local"),
            TestData.User(twoRentalsId, "two-rentals@test.local"));
        var listingId = Guid.NewGuid();
        await db.SeedAsync(TestData.Listing(listingId, ownerId, CategoryId, ListingStatus.Approved));
        await db.SeedAsync(
            TestData.Booking(Guid.NewGuid(), listingId, oneRentalId, TestData.Today, TestData.Today.AddDays(2), BookingStatus.Completed),
            TestData.Booking(Guid.NewGuid(), listingId, twoRentalsId, TestData.Today, TestData.Today.AddDays(2), BookingStatus.Completed),
            TestData.Booking(Guid.NewGuid(), listingId, twoRentalsId, TestData.Today.AddDays(3), TestData.Today.AddDays(5), BookingStatus.Completed),
            // A Pending booking must not count towards RentalCount/the sort.
            TestData.Booking(Guid.NewGuid(), listingId, oneRentalId, TestData.Today.AddDays(6), TestData.Today.AddDays(8), BookingStatus.Pending));

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).GetQueueAsync(new AdminUserQueueFilter { Status = "all", Sort = "rentals" });

        var ids = result.Value!.Items.Select(i => i.Id).Where(id => id == oneRentalId || id == twoRentalsId).ToList();
        Assert.Equal([twoRentalsId, oneRentalId], ids);
        Assert.Equal(2, result.Value.Items.Single(i => i.Id == twoRentalsId).RentalCount);
        Assert.Equal(1, result.Value.Items.Single(i => i.Id == oneRentalId).RentalCount);
    }

    [Fact]
    public async Task GetQueue_Sort_Flags_Falls_Back_To_Name_Order_When_No_Reports_Exist()
    {
        using var db = new SqliteTestDatabase();
        await SeedAdminAsync(db);
        var bId = new Guid("a1000000-0000-0000-0000-000000000039");
        var aId = new Guid("a1000000-0000-0000-0000-00000000003a");
        await db.SeedAsync(
            TestData.User(bId, "flagsb@test.local", firstName: "Bella", lastName: "Zed"),
            TestData.User(aId, "flagsa@test.local", firstName: "Anna", lastName: "Aaa"));

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).GetQueueAsync(new AdminUserQueueFilter { Status = "all", Sort = "flags" });

        var ids = result.Value!.Items.Select(i => i.Id).Where(id => id == aId || id == bId).ToList();
        Assert.Equal([aId, bId], ids);
        Assert.All(result.Value.Items, item => Assert.Equal(0, item.FlagCount));
    }

    [Fact]
    public async Task GetQueue_Sort_Flags_Orders_By_Real_Open_Report_Count_Descending()
    {
        // Wired placeholder: FlagCount must now be a real, DB-backed aggregate that drives the
        // ORDER BY/OFFSET itself (a post-page dictionary lookup could not influence which rows
        // land on the page) — the same correlated-subquery convention as Listings/Rentals.
        using var db = new SqliteTestDatabase();
        await SeedAdminAsync(db);
        var reporterId = new Guid("a1000000-0000-0000-0000-00000000003b");
        var noFlagsId = new Guid("a1000000-0000-0000-0000-00000000003c");
        var oneFlagId = new Guid("a1000000-0000-0000-0000-00000000003d");
        var twoFlagsId = new Guid("a1000000-0000-0000-0000-00000000003e");
        await db.SeedAsync(
            TestData.User(reporterId, "flags-reporter@test.local"),
            TestData.User(noFlagsId, "flags-none@test.local"),
            TestData.User(oneFlagId, "flags-one@test.local"),
            TestData.User(twoFlagsId, "flags-two@test.local"));
        await db.SeedAsync(
            TestData.Report(Guid.NewGuid(), ReportTargetType.User, oneFlagId, reporterId, "rudeMessages"),
            TestData.Report(Guid.NewGuid(), ReportTargetType.User, twoFlagsId, reporterId, "noShow"),
            TestData.Report(Guid.NewGuid(), ReportTargetType.User, twoFlagsId, reporterId, "spam"),
            // A Resolved report must not count towards the Open FlagCount/sort.
            TestData.Report(Guid.NewGuid(), ReportTargetType.User, oneFlagId, reporterId, "other", ReportStatus.Resolved));

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).GetQueueAsync(
            new AdminUserQueueFilter { Status = "all", Sort = "flags" });

        var ids = result.Value!.Items.Select(i => i.Id)
            .Where(id => id == noFlagsId || id == oneFlagId || id == twoFlagsId).ToList();
        Assert.Equal([twoFlagsId, oneFlagId, noFlagsId], ids);
        Assert.Equal(2, result.Value.Items.Single(i => i.Id == twoFlagsId).FlagCount);
        Assert.Equal(1, result.Value.Items.Single(i => i.Id == oneFlagId).FlagCount);
        Assert.Equal(0, result.Value.Items.Single(i => i.Id == noFlagsId).FlagCount);
    }

    [Fact]
    public async Task GetById_FlagCount_Reflects_Open_Reports_Against_This_User()
    {
        using var db = new SqliteTestDatabase();
        await SeedAdminAsync(db);
        var reporterId = new Guid("a1000000-0000-0000-0000-00000000003f");
        var targetId = new Guid("a1000000-0000-0000-0000-000000000041");
        await db.SeedAsync(
            TestData.User(reporterId, "flags-detail-reporter@test.local"),
            TestData.User(targetId, "flags-detail-target@test.local"));
        await db.SeedAsync(
            TestData.Report(Guid.NewGuid(), ReportTargetType.User, targetId, reporterId, "noShow"),
            TestData.Report(Guid.NewGuid(), ReportTargetType.User, targetId, reporterId, "spam"),
            // Dismissed must not count.
            TestData.Report(Guid.NewGuid(), ReportTargetType.User, targetId, reporterId, "other", ReportStatus.Dismissed),
            // A report against a DIFFERENT user must not count towards targetId's FlagCount.
            TestData.Report(Guid.NewGuid(), ReportTargetType.User, reporterId, targetId, "spam"));

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).GetByIdAsync(targetId);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value!.FlagCount);
    }

    // ---------- Pagination bounds ----------

    [Fact]
    public async Task GetQueue_Page_Below_One_Defaults_To_One()
    {
        using var db = new SqliteTestDatabase();
        await SeedAdminAsync(db);

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).GetQueueAsync(new AdminUserQueueFilter { Page = 0 });

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value!.Page);
    }

    [Fact]
    public async Task GetQueue_PageSize_Is_Clamped_To_Max()
    {
        using var db = new SqliteTestDatabase();
        await SeedAdminAsync(db);

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).GetQueueAsync(new AdminUserQueueFilter { PageSize = 5000 });

        Assert.True(result.IsSuccess);
        Assert.Equal(100, result.Value!.PageSize);
    }

    [Fact]
    public async Task GetQueue_TotalPages_Computed_From_TotalCount_And_PageSize()
    {
        using var db = new SqliteTestDatabase();
        await SeedAdminAsync(db);
        for (var i = 0; i < 5; i++)
        {
            await db.SeedAsync(TestData.User(Guid.NewGuid(), $"page{i}@test.local"));
        }

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).GetQueueAsync(new AdminUserQueueFilter { Status = "all", PageSize = 2 });

        Assert.True(result.IsSuccess);
        // 5 seeded + 1 admin = 6 total, page size 2 => 3 pages, 2 items on page 1.
        Assert.Equal(6, result.Value!.TotalCount);
        Assert.Equal(3, result.Value.TotalPages);
        Assert.Equal(2, result.Value.Items.Count);
    }

    // ---------- Summary counts (search-filtered, not status-filtered) ----------

    [Fact]
    public async Task GetQueue_Summary_Counts_Are_Search_Filtered_Not_Status_Filtered()
    {
        using var db = new SqliteTestDatabase();
        await SeedAdminAsync(db);
        await db.SeedAsync(
            TestData.User(Guid.NewGuid(), "sum-pending@test.local", firstName: "Summ", isIdConfirmed: false),
            TestData.User(Guid.NewGuid(), "sum-active@test.local", firstName: "Summ", isIdConfirmed: true),
            TestData.User(Guid.NewGuid(), "sum-suspended@test.local", firstName: "Summ", isBlocked: true, isIdConfirmed: true),
            // Not matched by the "Summ" search term below.
            TestData.User(Guid.NewGuid(), "other@test.local", firstName: "Other"));

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).GetQueueAsync(
            new AdminUserQueueFilter { Status = "suspended", Search = "Summ" });

        Assert.True(result.IsSuccess);
        // Status=suspended narrows Items to 1, but Summary reflects the search-filtered set only.
        Assert.Single(result.Value!.Items);
        Assert.Equal(3, result.Value.Summary.TotalUsers);
        Assert.Equal(1, result.Value.Summary.VerifiedCount);
        Assert.Equal(1, result.Value.Summary.PendingCount);
        Assert.Equal(1, result.Value.Summary.SuspendedCount);
    }

    // ---------- GetById ----------

    [Fact]
    public async Task GetById_Returns_The_Row()
    {
        using var db = new SqliteTestDatabase();
        await SeedAdminAsync(db);
        var userId = new Guid("a1000000-0000-0000-0000-000000000040");
        await db.SeedAsync(TestData.User(userId, "detail@test.local", firstName: "Det", lastName: "Ail"));

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).GetByIdAsync(userId);

        Assert.True(result.IsSuccess);
        Assert.Equal("Det", result.Value!.FirstName);
    }

    [Fact]
    public async Task GetById_Unknown_Id_Is_UserNotFound()
    {
        using var db = new SqliteTestDatabase();
        await SeedAdminAsync(db);

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).GetByIdAsync(Guid.NewGuid());

        Assert.False(result.IsSuccess);
        Assert.Equal("admin.user_not_found", result.Error!.Code);
    }

    // ---------- Verify: idempotency + moderation log ----------

    [Fact]
    public async Task Verify_Sets_IsIdConfirmed_And_Logs_Moderation_Entry()
    {
        using var db = new SqliteTestDatabase();
        await SeedAdminAsync(db);
        var userId = new Guid("a1000000-0000-0000-0000-000000000050");
        await db.SeedAsync(TestData.User(userId, "verify@test.local", isIdConfirmed: false));

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).VerifyAsync(userId);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value!.IsIdConfirmed);
        Assert.Equal(UserAccountStatus.Active, result.Value.Status);

        await using var verify = db.CreateContext();
        Assert.True((await verify.Users.FindAsync(userId))!.IsIdConfirmed);
        var log = await verify.ModerationLogEntries.SingleAsync(e => e.TargetId == userId);
        Assert.Equal(ModerationAction.UserVerified, log.Action);
        Assert.Equal(ModerationTargetType.User, log.TargetType);
        Assert.Equal(AdminId, log.ActorUserId);
    }

    [Fact]
    public async Task Verify_Is_Idempotent_When_Already_Verified()
    {
        using var db = new SqliteTestDatabase();
        await SeedAdminAsync(db);
        var userId = new Guid("a1000000-0000-0000-0000-000000000051");
        await db.SeedAsync(TestData.User(userId, "already-verified@test.local", isIdConfirmed: true));

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).VerifyAsync(userId);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value!.IsIdConfirmed);

        await using var verify = db.CreateContext();
        Assert.False(await verify.ModerationLogEntries.AnyAsync(e => e.TargetId == userId));
    }

    [Fact]
    public async Task Verify_Unknown_Id_Is_UserNotFound()
    {
        using var db = new SqliteTestDatabase();
        await SeedAdminAsync(db);

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).VerifyAsync(Guid.NewGuid());

        Assert.False(result.IsSuccess);
        Assert.Equal("admin.user_not_found", result.Error!.Code);
    }

    // ---------- Suspend / Reactivate: round trip, idempotency, moderation log ----------

    [Fact]
    public async Task Suspend_Sets_IsBlocked_And_Logs_Moderation_Entry()
    {
        using var db = new SqliteTestDatabase();
        await SeedAdminAsync(db);
        var userId = new Guid("a1000000-0000-0000-0000-000000000060");
        await db.SeedAsync(TestData.User(userId, "suspend@test.local"));

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).SuspendAsync(userId);

        Assert.True(result.IsSuccess);
        Assert.Equal(UserAccountStatus.Suspended, result.Value!.Status);

        await using var verify = db.CreateContext();
        Assert.True((await verify.Users.FindAsync(userId))!.IsBlocked);
        var log = await verify.ModerationLogEntries.SingleAsync(e => e.TargetId == userId);
        Assert.Equal(ModerationAction.UserSuspended, log.Action);
        Assert.Equal(ModerationTargetType.User, log.TargetType);
    }

    [Fact]
    public async Task Suspend_Is_Idempotent_When_Already_Suspended()
    {
        using var db = new SqliteTestDatabase();
        await SeedAdminAsync(db);
        var userId = new Guid("a1000000-0000-0000-0000-000000000061");
        await db.SeedAsync(TestData.User(userId, "already-suspended@test.local", isBlocked: true));

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).SuspendAsync(userId);

        Assert.True(result.IsSuccess);
        await using var verify = db.CreateContext();
        Assert.False(await verify.ModerationLogEntries.AnyAsync(e => e.TargetId == userId));
    }

    [Fact]
    public async Task Suspend_Then_Reactivate_Round_Trip()
    {
        using var db = new SqliteTestDatabase();
        await SeedAdminAsync(db);
        var userId = new Guid("a1000000-0000-0000-0000-000000000062");
        await db.SeedAsync(TestData.User(userId, "roundtrip@test.local"));

        await using (var context = db.CreateContext())
        {
            var suspendResult = await CreateService(context, AdminId).SuspendAsync(userId);
            Assert.True(suspendResult.IsSuccess);
            Assert.Equal(UserAccountStatus.Suspended, suspendResult.Value!.Status);
        }

        await using (var context = db.CreateContext())
        {
            var reactivateResult = await CreateService(context, AdminId).ReactivateAsync(userId);
            Assert.True(reactivateResult.IsSuccess);
            Assert.NotEqual(UserAccountStatus.Suspended, reactivateResult.Value!.Status);
        }

        await using var verify = db.CreateContext();
        Assert.False((await verify.Users.FindAsync(userId))!.IsBlocked);
        Assert.Equal(2, await verify.ModerationLogEntries.CountAsync(e => e.TargetId == userId));
        Assert.Contains(
            await verify.ModerationLogEntries.Where(e => e.TargetId == userId).Select(e => e.Action).ToListAsync(),
            a => a == ModerationAction.UserReactivated);
    }

    [Fact]
    public async Task Reactivate_Is_Idempotent_When_Already_Active()
    {
        using var db = new SqliteTestDatabase();
        await SeedAdminAsync(db);
        var userId = new Guid("a1000000-0000-0000-0000-000000000063");
        await db.SeedAsync(TestData.User(userId, "already-active@test.local", isBlocked: false));

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).ReactivateAsync(userId);

        Assert.True(result.IsSuccess);
        await using var verify = db.CreateContext();
        Assert.False(await verify.ModerationLogEntries.AnyAsync(e => e.TargetId == userId));
    }

    // ---------- Safety guards ----------

    [Fact]
    public async Task Suspend_Rejects_Self()
    {
        using var db = new SqliteTestDatabase();
        await SeedAdminAsync(db);

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).SuspendAsync(AdminId);

        Assert.False(result.IsSuccess);
        Assert.Equal("admin.cannot_suspend_self", result.Error!.Code);

        await using var verify = db.CreateContext();
        Assert.False((await verify.Users.FindAsync(AdminId))!.IsBlocked);
    }

    [Fact]
    public async Task Suspend_Rejects_Another_Admin()
    {
        using var db = new SqliteTestDatabase();
        await SeedAdminAsync(db);
        await db.SeedAsync(TestData.User(OtherAdminId, "other-admin@test.local", role: UserRole.Admin, isIdConfirmed: true));

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).SuspendAsync(OtherAdminId);

        Assert.False(result.IsSuccess);
        Assert.Equal("admin.cannot_suspend_admin", result.Error!.Code);

        await using var verify = db.CreateContext();
        Assert.False((await verify.Users.FindAsync(OtherAdminId))!.IsBlocked);
    }

    [Fact]
    public async Task Suspend_Unknown_Id_Is_UserNotFound()
    {
        using var db = new SqliteTestDatabase();
        await SeedAdminAsync(db);

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).SuspendAsync(Guid.NewGuid());

        Assert.False(result.IsSuccess);
        Assert.Equal("admin.user_not_found", result.Error!.Code);
    }

    // ---------- Admin-role enforcement (defence in depth) ----------

    [Fact]
    public async Task GetQueue_By_Non_Admin_Is_Forbidden()
    {
        using var db = new SqliteTestDatabase();
        await SeedAdminAsync(db);
        var nonAdminId = new Guid("a1000000-0000-0000-0000-000000000070");
        await db.SeedAsync(TestData.User(nonAdminId, "nonadmin@test.local"));

        await using var context = db.CreateContext();
        var result = await CreateService(context, nonAdminId).GetQueueAsync(new AdminUserQueueFilter());

        Assert.False(result.IsSuccess);
        Assert.Equal("admin.forbidden", result.Error!.Code);
    }

    [Fact]
    public async Task GetQueue_Unauthenticated_Is_Unauthenticated()
    {
        using var db = new SqliteTestDatabase();
        await SeedAdminAsync(db);

        await using var context = db.CreateContext();
        var result = await CreateService(context, currentUserId: default).GetQueueAsync(new AdminUserQueueFilter());

        Assert.False(result.IsSuccess);
        Assert.Equal("admin.unauthenticated", result.Error!.Code);
    }
}
