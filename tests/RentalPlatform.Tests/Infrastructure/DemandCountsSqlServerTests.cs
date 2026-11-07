using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using RentalPlatform.Application.DTOs;
using RentalPlatform.Application.Services;
using RentalPlatform.Domain;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Infrastructure.Persistence;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Infrastructure;

// P3-3 demand counters against a REAL SQL Server (plan section 4: #2, #3, #4, #5, #6, #7, #8, #14).
// The concurrency gate (#1/#1b) lives in DemandCountsSqlServerConcurrencyTests.
public sealed class DemandCountsSqlServerTests
{
    private static readonly Guid OwnerId = new("f2000000-0000-0000-0000-000000000001");
    private static readonly Guid RenterId = new("f2000000-0000-0000-0000-000000000002");
    private static readonly Guid AdminId = new("f2000000-0000-0000-0000-000000000004");
    private static readonly Guid CategoryId = new("f2000000-0000-0000-0000-000000000003");

    private static readonly DateTime Noon = new(2030, 3, 1, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly NoonDay = new(2030, 3, 1);

    private static async Task<Guid> SeedBaseAsync(SqlServerTestDatabase db, Guid? districtId)
    {
        var listingId = Guid.NewGuid();
        var listing = TestData.Listing(listingId, OwnerId, CategoryId);
        listing.DistrictId = districtId;
        await db.SeedAsync(
            TestData.User(OwnerId, "owner@demand.local"),
            TestData.User(RenterId, "renter@demand.local"),
            TestData.User(AdminId, "admin@demand.local", role: UserRole.Admin),
            TestData.Category(CategoryId));
        await db.SeedAsync(listing);
        return listingId;
    }

    private static Booking NewBooking(Guid listingId, int startOffset, DateTime? createdAt = null)
    {
        var booking = TestData.Booking(
            Guid.NewGuid(), listingId, RenterId,
            new DateOnly(2030, 6, 1).AddDays(startOffset), new DateOnly(2030, 6, 1).AddDays(startOffset + 1),
            BookingStatus.Pending);
        booking.CreatedAt = createdAt ?? Noon;
        booking.UpdatedAt = booking.CreatedAt;
        return booking;
    }

    private static async Task<bool> CreateAsync(SqlServerTestDatabase db, Booking booking, Guid? districtId, params IInterceptor[] interceptors)
    {
        await using var context = db.CreateContext(interceptors);
        return await new BookingsStore(context).TryCreateBookingAsync(booking, districtId, CategoryId);
    }

    private static BookingsService Service(AppDbContext context, Guid userId) => new(
        new FakeCurrentUserContext(userId),
        new BookingsStore(context),
        new FakeNotificationEmitter(),
        new FakeChatSystemMessageEmitter(),
        new ReviewsStore(context),
        new ConversationsStore(context, NullLogger<ConversationsStore>.Instance));

    private static async Task<List<DemandDailyCount>> RowsAsync(SqlServerTestDatabase db)
    {
        await using var verify = db.CreateContext();
        return await verify.DemandDailyCounts.AsNoTracking().ToListAsync();
    }

    // ---------- #2 rollback ----------

    [SqlServerFact]
    public async Task Test2_An_Overlap_Refusal_Leaves_The_Counter_Unchanged()
    {
        using var db = new SqlServerTestDatabase();
        var listingId = await SeedBaseAsync(db, TestData.KentronDistrictId);

        Assert.True(await CreateAsync(db, NewBooking(listingId, 0), TestData.KentronDistrictId));
        Assert.False(await CreateAsync(db, NewBooking(listingId, 0), TestData.KentronDistrictId));

        Assert.Equal(1, Assert.Single(await RowsAsync(db)).BookingRequests);
    }

    [SqlServerFact]
    public async Task Test2_A_Deleted_Owner_Refusal_Counts_Nothing()
    {
        using var db = new SqlServerTestDatabase();
        var listingId = await SeedBaseAsync(db, TestData.KentronDistrictId);
        await using (var fix = db.CreateContext())
        {
            await fix.Users.Where(user => user.Id == OwnerId).ExecuteUpdateAsync(
                update => update.SetProperty(user => user.DeletedAt, Noon));
        }

        Assert.False(await CreateAsync(db, NewBooking(listingId, 0), TestData.KentronDistrictId));

        Assert.Empty(await RowsAsync(db));
    }

    [SqlServerFact]
    public async Task Test2_A_Failure_After_The_Increment_And_Before_Commit_Rolls_Back_Booking_And_Count()
    {
        using var db = new SqlServerTestDatabase();
        var listingId = await SeedBaseAsync(db, TestData.KentronDistrictId);
        Assert.True(await CreateAsync(db, NewBooking(listingId, 0), TestData.KentronDistrictId)); // cell exists, count = 1

        var failing = new FailBeforeCommitInterceptor();
        var recorder = new CommandRecorder();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateAsync(db, NewBooking(listingId, 5), TestData.KentronDistrictId, failing, recorder));

        // The UPDATE ran (so the failure really was after the increment) and was undone with the booking.
        Assert.Contains(recorder.Texts, text => text.Contains("DemandDailyCounts", StringComparison.Ordinal)
            && text.Contains("UPDATE", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, Assert.Single(await RowsAsync(db)).BookingRequests);
        await using var verify = db.CreateContext();
        Assert.Equal(1, await verify.Bookings.CountAsync());
    }

    [SqlServerFact]
    public async Task Test2_A_Failure_After_The_First_Row_Insert_And_Before_Commit_Leaves_No_Row()
    {
        using var db = new SqlServerTestDatabase();
        var listingId = await SeedBaseAsync(db, TestData.KentronDistrictId);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateAsync(db, NewBooking(listingId, 0), TestData.KentronDistrictId, new FailBeforeCommitInterceptor()));

        Assert.Empty(await RowsAsync(db));
        await using var verify = db.CreateContext();
        Assert.Equal(0, await verify.Bookings.CountAsync());
    }

    // ---------- #3 duplicate submit ----------

    [SqlServerFact]
    public async Task Test3_A_Duplicate_Submit_Creates_One_Booking_And_Counts_Once()
    {
        using var db = new SqlServerTestDatabase();
        var listingId = await SeedBaseAsync(db, TestData.KentronDistrictId);
        var request = new CreateBookingRequest
        {
            ListingId = listingId,
            StartDate = TestData.Today.AddDays(10),
            EndDate = TestData.Today.AddDays(12)
        };

        await using var first = db.CreateContext();
        Assert.True((await Service(first, RenterId).CreateAsync(request)).IsSuccess);
        await using var second = db.CreateContext();
        var duplicate = await Service(second, RenterId).CreateAsync(request);

        Assert.Equal("booking.overlap", duplicate.Error!.Code);
        Assert.Equal(1, Assert.Single(await RowsAsync(db)).BookingRequests);
    }

    // ---------- #4 owner moves home between two bookings ----------

    [SqlServerFact]
    public async Task Test4_A_Move_Between_Two_Bookings_Counts_Each_In_Its_Own_District()
    {
        using var db = new SqlServerTestDatabase();
        var listingId = await SeedBaseAsync(db, TestData.KentronDistrictId);

        await using (var a = db.CreateContext())
        {
            Assert.True((await Service(a, RenterId).CreateAsync(new CreateBookingRequest
            {
                ListingId = listingId, StartDate = TestData.Today.AddDays(10), EndDate = TestData.Today.AddDays(11)
            })).IsSuccess);
        }

        await using (var move = db.CreateContext())
        {
            await move.Listings.Where(listing => listing.Id == listingId).ExecuteUpdateAsync(
                update => update.SetProperty(listing => listing.DistrictId, TestData.ArabkirDistrictId));
        }

        await using (var b = db.CreateContext())
        {
            Assert.True((await Service(b, RenterId).CreateAsync(new CreateBookingRequest
            {
                ListingId = listingId, StartDate = TestData.Today.AddDays(20), EndDate = TestData.Today.AddDays(21)
            })).IsSuccess);
        }

        var rows = await RowsAsync(db);
        Assert.Equal(2, rows.Count);
        Assert.Equal(1, rows.Single(row => row.DistrictId == TestData.KentronDistrictId).BookingRequests);
        Assert.Equal(1, rows.Single(row => row.DistrictId == TestData.ArabkirDistrictId).BookingRequests);
    }

    // ---------- #5 / #6 same cell, NULL district ----------

    [SqlServerFact]
    public async Task Test5_A_Second_Booking_On_An_Existing_Cell_Increments_The_Same_Row()
    {
        using var db = new SqlServerTestDatabase();
        var listingId = await SeedBaseAsync(db, TestData.KentronDistrictId);

        Assert.True(await CreateAsync(db, NewBooking(listingId, 0), TestData.KentronDistrictId));
        Assert.True(await CreateAsync(db, NewBooking(listingId, 5), TestData.KentronDistrictId));

        var row = Assert.Single(await RowsAsync(db));
        Assert.Equal(2, row.BookingRequests);
        Assert.Equal(NoonDay, row.Day);
    }

    [SqlServerFact]
    public async Task Test6_Null_District_Bookings_Share_One_Row_And_The_Index_Has_No_Filter()
    {
        using var db = new SqlServerTestDatabase();
        var listingId = await SeedBaseAsync(db, null);

        Assert.True(await CreateAsync(db, NewBooking(listingId, 0), null));
        Assert.True(await CreateAsync(db, NewBooking(listingId, 5), null));

        var row = Assert.Single(await RowsAsync(db));
        Assert.Null(row.DistrictId);
        Assert.Equal(2, row.BookingRequests);

        // The unique index must be unfiltered, or NULL-district rows could duplicate.
        await using var context = db.CreateContext();
        var filtered = await context.Database.SqlQueryRaw<int>(
            "SELECT CAST(has_filter AS int) AS [Value] FROM sys.indexes WHERE name = 'IX_DemandDailyCounts_Day_DistrictId_CategoryId'")
            .ToListAsync();
        Assert.Equal(new[] { 0 }, filtered);

        // ...and a raw duplicate NULL-district row is refused by it.
        await Assert.ThrowsAnyAsync<DbUpdateException>(async () =>
        {
            context.DemandDailyCounts.Add(new DemandDailyCount
            {
                Id = Guid.NewGuid(), Day = NoonDay, DistrictId = null, CategoryId = CategoryId, BookingRequests = 1
            });
            await context.SaveChangesAsync();
        });
    }

    // ---------- #7 category delete ----------

    [SqlServerFact]
    public async Task Test7_Deleting_A_Category_With_Counts_Succeeds_And_History_Keeps_The_Old_Id()
    {
        using var db = new SqlServerTestDatabase();
        var listingId = await SeedBaseAsync(db, TestData.KentronDistrictId);
        var emptyCategoryId = Guid.NewGuid();
        var targetCategoryId = Guid.NewGuid();
        await db.SeedAsync(
            TestData.Category(emptyCategoryId, name: "Empty", slug: "empty-demand"),
            TestData.Category(targetCategoryId, name: "Target", slug: "target-demand"));
        await db.SeedAsync(new DemandDailyCount
        {
            Id = Guid.NewGuid(), Day = NoonDay, DistrictId = null, CategoryId = emptyCategoryId, BookingRequests = 3
        });
        Assert.True(await CreateAsync(db, NewBooking(listingId, 0), TestData.KentronDistrictId));

        await using (var direct = db.CreateContext())
        {
            var result = await CategoryService(direct).DeleteAsync(emptyCategoryId, reassignToCategoryId: null);
            Assert.True(result.IsSuccess);
        }

        await using (var reassign = db.CreateContext())
        {
            var result = await CategoryService(reassign).DeleteAsync(CategoryId, targetCategoryId);
            Assert.True(result.IsSuccess);
        }

        var rows = await RowsAsync(db);
        Assert.Contains(rows, row => row.CategoryId == emptyCategoryId && row.BookingRequests == 3);
        Assert.Contains(rows, row => row.CategoryId == CategoryId && row.BookingRequests == 1);
        Assert.Equal(2, rows.Count);
    }

    private static AdminCategoriesService CategoryService(AppDbContext context) => new(
        new FakeCurrentUserContext(AdminId),
        new AdminCategoriesStore(context),
        new ModerationLogStore(context, NullLogger<ModerationLogStore>.Instance));

    // ---------- #8 midnight ----------

    [SqlServerFact]
    public async Task Test8_The_Yerevan_Midnight_Boundary_Is_20_00_UTC()
    {
        using var db = new SqlServerTestDatabase();
        var listingId = await SeedBaseAsync(db, TestData.KentronDistrictId);
        var justBefore = new DateTime(2030, 3, 1, 19, 59, 59, DateTimeKind.Utc);
        var atMidnight = new DateTime(2030, 3, 1, 20, 0, 0, DateTimeKind.Utc);

        Assert.True(await CreateAsync(db, NewBooking(listingId, 0, justBefore), TestData.KentronDistrictId));
        Assert.True(await CreateAsync(db, NewBooking(listingId, 5, atMidnight), TestData.KentronDistrictId));

        var rows = (await RowsAsync(db)).OrderBy(row => row.Day).ToList();
        Assert.Equal(new[] { new DateOnly(2030, 3, 1), new DateOnly(2030, 3, 2) }, rows.Select(row => row.Day));
        Assert.All(rows, row => Assert.Equal(1, row.BookingRequests));
    }

    // ---------- #14 backfill (and #8: the SQL formula agrees with the C# helper) ----------

    [SqlServerFact]
    public async Task Test14_The_Backfill_Groups_Existing_Bookings_By_Yerevan_Day_District_And_Category()
    {
        using var db = new SqlServerTestDatabase();
        db.MigrateTo("20261106124905_AddUserDeletedAt");

        var otherCategory = Guid.NewGuid();
        var listingKentron = Guid.NewGuid();
        var listingKentron2 = Guid.NewGuid();
        var listingArabkir = Guid.NewGuid();
        var listingNoDistrict = Guid.NewGuid();
        var listingOtherCategory = Guid.NewGuid();
        await db.SeedLegacyUsersAsync((OwnerId, "owner@backfill.local", 0), (RenterId, "renter@backfill.local", 0));
        await db.SeedLegacyCategoryAsync(CategoryId, "Blocks", "blocks-backfill");
        await db.SeedLegacyCategoryAsync(otherCategory, "Other", "other-backfill");
        await db.SeedLegacyListingAsync(listingKentron, OwnerId, CategoryId, null, null, 10);
        await db.SeedLegacyListingAsync(listingKentron2, OwnerId, CategoryId, null, null, 10);
        await db.SeedLegacyListingAsync(listingArabkir, OwnerId, CategoryId, null, null, 10);
        await db.SeedLegacyListingAsync(listingNoDistrict, OwnerId, CategoryId, null, null, 10);
        await db.SeedLegacyListingAsync(listingOtherCategory, OwnerId, otherCategory, null, null, 10);
        await SetDistrictAsync(db, listingKentron, TestData.KentronDistrictId);
        await SetDistrictAsync(db, listingKentron2, TestData.KentronDistrictId);
        await SetDistrictAsync(db, listingArabkir, TestData.ArabkirDistrictId);
        await SetDistrictAsync(db, listingOtherCategory, TestData.KentronDistrictId);

        var d = new DateTime(2030, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        var beforeMidnight = d.AddHours(19).AddMinutes(59).AddSeconds(59);
        await InsertLegacyBookingAsync(db, listingKentron, beforeMidnight, BookingStatus.Pending);      // 03-01 Yerevan
        await InsertLegacyBookingAsync(db, listingKentron2, d.AddHours(5), BookingStatus.Rejected);     // 03-01, same cell: every status counts
        await InsertLegacyBookingAsync(db, listingKentron, d.AddHours(20), BookingStatus.Completed);    // 20:00Z is already 03-02 in Yerevan
        await InsertLegacyBookingAsync(db, listingArabkir, d.AddHours(1), BookingStatus.Approved);      // 03-01 Arabkir
        await InsertLegacyBookingAsync(db, listingNoDistrict, d.AddHours(1), BookingStatus.Cancelled);  // 03-01 NULL district
        await InsertLegacyBookingAsync(db, listingNoDistrict, d.AddHours(2), BookingStatus.Expired);    // 03-01 NULL district
        await InsertLegacyBookingAsync(db, listingOtherCategory, d.AddHours(3), BookingStatus.Active);  // 03-01 other category

        db.MigrateToLatest();

        var rows = await RowsAsync(db);
        int Count(DateOnly day, Guid? district, Guid category) =>
            rows.SingleOrDefault(row => row.Day == day && row.DistrictId == district && row.CategoryId == category)?.BookingRequests ?? 0;
        var mar1 = new DateOnly(2030, 3, 1);
        var mar2 = new DateOnly(2030, 3, 2);
        Assert.Equal(2, Count(mar1, TestData.KentronDistrictId, CategoryId));
        Assert.Equal(1, Count(mar2, TestData.KentronDistrictId, CategoryId));
        Assert.Equal(1, Count(mar1, TestData.ArabkirDistrictId, CategoryId));
        Assert.Equal(2, Count(mar1, null, CategoryId));
        Assert.Equal(1, Count(mar1, TestData.KentronDistrictId, otherCategory));
        Assert.Equal(5, rows.Count);
        Assert.Equal(7, rows.Sum(row => row.BookingRequests));

        // The C# helper agrees with the SQL formula on both sides of the boundary.
        Assert.Equal(mar1, YerevanCalendar.DateOf(beforeMidnight));
        Assert.Equal(mar2, YerevanCalendar.DateOf(d.AddHours(20)));
    }

    private static async Task SetDistrictAsync(SqlServerTestDatabase db, Guid listingId, Guid districtId)
    {
        await using var context = db.CreateContext();
        await context.Database.ExecuteSqlRawAsync("UPDATE [Listings] SET [DistrictId] = {0} WHERE [Id] = {1}", districtId, listingId);
    }

    // Raw SQL naming only the columns that exist at the pre-migration schema state.
    private static async Task InsertLegacyBookingAsync(SqlServerTestDatabase db, Guid listingId, DateTime createdAt, BookingStatus status)
    {
        await using var context = db.CreateContext();
        await context.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO [Bookings] ([Id], [ListingId], [RenterId], [StartDate], [EndDate], [TotalPrice], [Status], [ExpiresAt], [CreatedAt], [UpdatedAt])
            VALUES ({0}, {1}, {2}, '2031-01-01', '2031-01-02', 100, {3}, {4}, {4}, {4});
            """,
            Guid.NewGuid(), listingId, RenterId, (int)status, createdAt);
    }

    // Throws on the transaction commit: i.e. after every statement of the create, before it is durable.
    private sealed class FailBeforeCommitInterceptor : DbTransactionInterceptor
    {
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("forced failure before commit");
    }

    private sealed class CommandRecorder : DbCommandInterceptor
    {
        public List<string> Texts { get; } = new();

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Texts.Add(command.CommandText);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
