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

namespace RentalPlatform.Tests.Bookings;

// P3-3 demand counters on the SQLite test host (plan section 4: #5, #8 helper, #13, and the generated
// predicate). The real-SQL-Server behaviour is in Infrastructure/DemandCountsSqlServer*Tests.
public sealed class DemandCountsTests
{
    private static readonly Guid OwnerId = new("f3000000-0000-0000-0000-000000000001");
    private static readonly Guid RenterId = new("f3000000-0000-0000-0000-000000000002");
    private static readonly Guid CategoryId = new("f3000000-0000-0000-0000-000000000003");
    private static readonly Guid ListingId = new("f3000000-0000-0000-0000-000000000004");

    private static readonly DateOnly Today = TestData.Today;

    private static async Task SeedAsync(SqliteTestDatabase db, Guid? districtId = null)
    {
        await db.SeedAsync(
            TestData.User(OwnerId, "owner@demand-sqlite.local"),
            TestData.User(RenterId, "renter@demand-sqlite.local"),
            TestData.Category(CategoryId));
        var listing = TestData.Listing(ListingId, OwnerId, CategoryId);
        listing.DistrictId = districtId;
        await db.SeedAsync(listing);
    }

    private static BookingsService Service(AppDbContext context, Guid userId) => new(
        new FakeCurrentUserContext(userId),
        new BookingsStore(context),
        new FakeNotificationEmitter(),
        new FakeChatSystemMessageEmitter(),
        new ReviewsStore(context),
        new ConversationsStore(context, NullLogger<ConversationsStore>.Instance));

    private static async Task<List<DemandDailyCount>> RowsAsync(SqliteTestDatabase db)
    {
        await using var verify = db.CreateContext();
        return await verify.DemandDailyCounts.AsNoTracking().ToListAsync();
    }

    [Fact]
    public async Task A_Created_Booking_Is_Counted_On_Its_Yerevan_Day_Under_The_Listing_District_And_Category()
    {
        using var db = new SqliteTestDatabase();
        await SeedAsync(db, TestData.KentronDistrictId);

        await using var context = db.CreateContext();
        var before = DateTime.UtcNow;
        var result = await Service(context, RenterId).CreateAsync(new CreateBookingRequest
        {
            ListingId = ListingId, StartDate = Today.AddDays(5), EndDate = Today.AddDays(6)
        });
        var after = DateTime.UtcNow;

        Assert.True(result.IsSuccess);
        var row = Assert.Single(await RowsAsync(db));
        Assert.Equal(TestData.KentronDistrictId, row.DistrictId);
        Assert.Equal(CategoryId, row.CategoryId);
        Assert.Equal(1, row.BookingRequests);
        Assert.Contains(row.Day, new[] { YerevanCalendar.DateOf(before), YerevanCalendar.DateOf(after) });
    }

    [Fact]
    public async Task Test5_A_Second_Booking_On_An_Existing_Cell_Increments_The_Same_Row()
    {
        using var db = new SqliteTestDatabase();
        await SeedAsync(db, TestData.KentronDistrictId);

        foreach (var offset in new[] { 5, 10 })
        {
            await using var context = db.CreateContext();
            Assert.True((await Service(context, RenterId).CreateAsync(new CreateBookingRequest
            {
                ListingId = ListingId, StartDate = Today.AddDays(offset), EndDate = Today.AddDays(offset + 1)
            })).IsSuccess);
        }

        Assert.Equal(2, Assert.Single(await RowsAsync(db)).BookingRequests);
    }

    [Fact]
    public async Task A_Refused_Booking_Is_Not_Counted()
    {
        using var db = new SqliteTestDatabase();
        await SeedAsync(db, TestData.KentronDistrictId);
        var request = new CreateBookingRequest { ListingId = ListingId, StartDate = Today.AddDays(5), EndDate = Today.AddDays(6) };

        await using var first = db.CreateContext();
        Assert.True((await Service(first, RenterId).CreateAsync(request)).IsSuccess);
        await using var second = db.CreateContext();
        Assert.False((await Service(second, RenterId).CreateAsync(request)).IsSuccess);

        Assert.Equal(1, Assert.Single(await RowsAsync(db)).BookingRequests);
    }

    [Fact]
    public async Task The_Null_District_Update_Predicate_Is_A_Literal_IS_NULL_And_The_Populated_One_Is_An_Equality()
    {
        foreach (var districtId in new Guid?[] { null, TestData.KentronDistrictId })
        {
            using var db = new SqliteTestDatabase();
            await SeedAsync(db, districtId);
            var recorder = new UpdateRecorder();
            var booking = TestData.Booking(Guid.NewGuid(), ListingId, RenterId, Today.AddDays(5), Today.AddDays(6), BookingStatus.Pending);

            await using (var context = db.CreateContext(recorder))
            {
                Assert.True(await new BookingsStore(context).TryCreateBookingAsync(booking, districtId, CategoryId));
            }

            var update = Assert.Single(recorder.Texts, text => text.Contains("DemandDailyCounts", StringComparison.Ordinal));
            if (districtId is null)
            {
                Assert.Contains("\"DistrictId\" IS NULL", update);
            }
            else
            {
                Assert.DoesNotContain("IS NULL", update);
                Assert.Contains("\"DistrictId\" =", update);
            }
        }
    }

    [Fact]
    public async Task Test13_The_Lifecycle_After_Creation_Never_Changes_The_Counters()
    {
        using var db = new SqliteTestDatabase();
        await SeedAsync(db, TestData.KentronDistrictId);

        // One counted request through the service.
        Guid createdId;
        await using (var create = db.CreateContext())
        {
            var created = await Service(create, RenterId).CreateAsync(new CreateBookingRequest
            {
                ListingId = ListingId, StartDate = Today.AddDays(30), EndDate = Today.AddDays(31)
            });
            Assert.True(created.IsSuccess);
            createdId = created.Value!.Id;
        }

        var snapshot = await RowsAsync(db);
        Assert.Equal(1, Assert.Single(snapshot).BookingRequests);

        // Approve (owner), then cancel (renter) the counted one.
        await using (var ctx = db.CreateContext())
        {
            Assert.True((await Service(ctx, OwnerId).ApproveAsync(createdId)).IsSuccess);
        }

        await using (var ctx = db.CreateContext())
        {
            Assert.True((await Service(ctx, RenterId).CancelAsync(createdId)).IsSuccess);
        }

        // Reject, activate and complete bookings seeded directly (not counted).
        var rejectId = Guid.NewGuid();
        var activeId = Guid.NewGuid();
        var expiredId = Guid.NewGuid();
        await db.SeedAsync(
            TestData.Booking(rejectId, ListingId, RenterId, Today.AddDays(40), Today.AddDays(41), BookingStatus.Pending),
            TestData.Booking(activeId, ListingId, RenterId, Today.AddDays(-5), Today.AddDays(-1), BookingStatus.Approved),
            TestData.Booking(expiredId, ListingId, RenterId, Today.AddDays(50), Today.AddDays(51), BookingStatus.Pending,
                expiresAt: DateTime.UtcNow.AddHours(-1)));

        await using (var ctx = db.CreateContext())
        {
            Assert.True((await Service(ctx, OwnerId).RejectAsync(rejectId, "no")).IsSuccess);
        }

        await using (var ctx = db.CreateContext())
        {
            Assert.True((await Service(ctx, OwnerId).MarkActiveAsync(activeId)).IsSuccess);
        }

        await using (var ctx = db.CreateContext())
        {
            Assert.True((await Service(ctx, OwnerId).CompleteAsync(activeId)).IsSuccess);
        }

        await using (var ctx = db.CreateContext())
        {
            var swept = await new BookingsStore(ctx).ExpirePendingAsync(DateTime.UtcNow);
            Assert.Contains(swept, booking => booking.Id == expiredId);
        }

        var after = Assert.Single(await RowsAsync(db));
        Assert.Equal(snapshot[0].Id, after.Id);
        Assert.Equal(1, after.BookingRequests);
    }

    [Fact]
    public void YerevanCalendar_DateOf_Switches_Day_At_20_00_UTC()
    {
        Assert.Equal(new DateOnly(2030, 3, 1), YerevanCalendar.DateOf(new DateTime(2030, 3, 1, 19, 59, 59, DateTimeKind.Utc)));
        Assert.Equal(new DateOnly(2030, 3, 2), YerevanCalendar.DateOf(new DateTime(2030, 3, 1, 20, 0, 0, DateTimeKind.Utc)));
        Assert.Equal(new DateOnly(2030, 3, 2), YerevanCalendar.DateOf(new DateTimeOffset(2030, 3, 1, 20, 0, 0, TimeSpan.Zero)));
    }

    private sealed class UpdateRecorder : DbCommandInterceptor
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
