using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Infrastructure.Persistence;
using RentalPlatform.Tests.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace RentalPlatform.Tests.Infrastructure;

// P3-3 HARD GATE (plan section 9): the first-row insert of a demand counter under SERIALIZABLE is only
// proven safe by racing real BookingsStore.TryCreateBookingAsync calls on a REAL SQL Server. SQLite is
// single-writer and cannot show a key-range lock, a duplicate-key error (2601/2627) or a deadlock (1205).
//
// Each test fires 10 concurrent creates on 10 DIFFERENT listings (so the booking overlap check never
// serialises them) that all share one (day, district, category) counter cell.
//   #1  / #1b : populated DistrictId / NULL DistrictId, with NO pre-existing counter row (first-row race)
//   variant B : the same, but the row already exists
[Collection(SqlServerConcurrencyCollection.Name)]
public sealed class DemandCountsSqlServerConcurrencyTests
{
    private const int Racers = 10;

    private static readonly Guid OwnerId = new("f1000000-0000-0000-0000-000000000001");
    private static readonly Guid RenterId = new("f1000000-0000-0000-0000-000000000002");
    private static readonly Guid CategoryId = new("f1000000-0000-0000-0000-000000000003");

    private static readonly DateTime CreatedAt = new(2030, 3, 1, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly CountDay = new(2030, 3, 1);

    private readonly ITestOutputHelper _output;

    public DemandCountsSqlServerConcurrencyTests(ITestOutputHelper output) => _output = output;

    [SqlServerFact]
    public Task Test1_Populated_District_First_Row_Race_Counts_Exactly() =>
        RunAsync(TestData.KentronDistrictId, preExistingRow: false);

    [SqlServerFact]
    public Task Test1_Populated_District_Existing_Row_Counts_Exactly() =>
        RunAsync(TestData.KentronDistrictId, preExistingRow: true);

    [SqlServerFact]
    public Task Test1b_Null_District_First_Row_Race_Counts_Exactly() =>
        RunAsync(null, preExistingRow: false);

    [SqlServerFact]
    public Task Test1b_Null_District_Existing_Row_Counts_Exactly() =>
        RunAsync(null, preExistingRow: true);

    private async Task RunAsync(Guid? districtId, bool preExistingRow)
    {
        using var db = new SqlServerTestDatabase();
        await db.SeedAsync(
            TestData.User(OwnerId, "owner@demand-race.local"),
            TestData.User(RenterId, "renter@demand-race.local"),
            TestData.Category(CategoryId));

        var listingIds = Enumerable.Range(0, Racers + 1).Select(_ => Guid.NewGuid()).ToList();
        foreach (var listingId in listingIds)
        {
            var listing = TestData.Listing(listingId, OwnerId, CategoryId);
            listing.DistrictId = districtId;
            await db.SeedAsync(listing);
        }

        await SeedBoundaryBookingsAsync(db, listingIds);

        await SeedFillerBookingsAsync(db);

        var expected = Racers;
        if (preExistingRow)
        {
            // One sequential create first, so the cell exists when the racers arrive.
            await using var first = db.CreateContext();
            Assert.True(await new BookingsStore(first).TryCreateBookingAsync(
                NewBooking(listingIds[Racers]), districtId, CategoryId));
            expected++;
        }

        var gate = new TaskCompletionSource();
        var racers = listingIds.Take(Racers).Select(listingId => Task.Run(async () =>
        {
            await gate.Task;
            await using var context = db.CreateContext();
            return await new BookingsStore(context).TryCreateBookingAsync(NewBooking(listingId), districtId, CategoryId);
        })).ToList();

        gate.SetResult();
        var outcomes = new List<(bool Added, Exception? Error)>();
        foreach (var racer in racers)
        {
            try { outcomes.Add((await racer, null)); }
            catch (Exception ex) { outcomes.Add((false, ex)); }
        }

        var errors = outcomes.Where(o => o.Error is not null).Select(o => o.Error!).ToList();
        var sqlNumbers = errors.Select(SqlNumberOf).ToList();
        foreach (var error in errors)
        {
            _output.WriteLine($"racer error (SQL {SqlNumberOf(error)}): {error.GetType().Name}: {error.Message}");
        }

        // Duplicate-key errors mean the first-row insert is NOT safe: never acceptable.
        Assert.DoesNotContain(2601, sqlNumbers);
        Assert.DoesNotContain(2627, sqlNumbers);
        // Anything but a deadlock victim is an unexpected exception.
        Assert.True(sqlNumbers.All(number => number == 1205), "unexpected exceptions: " + string.Join(" | ", errors.Select(e => e.Message)));

        // The invariant, always: counter == committed bookings, one row for the key.
        await using var verify = db.CreateContext();
        var bookings = await verify.Bookings.AsNoTracking().CountAsync(b => b.CreatedAt == CreatedAt);
        var rows = await verify.DemandDailyCounts.AsNoTracking().ToListAsync();
        var row = Assert.Single(rows);
        Assert.Equal(CountDay, row.Day);
        Assert.Equal(districtId, row.DistrictId);
        Assert.Equal(CategoryId, row.CategoryId);
        Assert.Equal(bookings, row.BookingRequests);
        Assert.Equal(outcomes.Count(o => o.Added) + (preExistingRow ? 1 : 0), bookings);

        // A deadlock victim rolled back both writes (the invariant above held), but the gate asks for all
        // of them to commit: any 1205 fails the test.
        Assert.Empty(sqlNumbers);
        Assert.Equal(expected, bookings);
        Assert.Equal(expected, row.BookingRequests);
    }

    // SERIALIZABLE key-range locks of the existing overlap check end at the NEXT key of the index
    // (ListingId, Status, StartDate, EndDate). A listing with no bookings of its own has no such key, so
    // listings that neighbour each other in the index share the same gap: every racer then holds a shared
    // range lock on the SAME next key and waits to insert into it, and they deadlock (SQL 1205) in the
    // existing create path alone (verified with the counter call disabled). That is a property of the
    // existing design for never-booked listings, not of the counter. Giving each listing one far-future
    // row per checked status (Pending/Approved/Active) bounds each racer's ranges by its OWN rows, so the
    // racers contend only on the counter cell - which is what this test measures. These rows are not part
    // of the count.
    private static async Task SeedBoundaryBookingsAsync(SqlServerTestDatabase db, IEnumerable<Guid> listingIds)
    {
        foreach (var listingId in listingIds)
        {
            foreach (var status in new[] { BookingStatus.Pending, BookingStatus.Approved, BookingStatus.Active })
            {
                // An already-expired Pending row does not block (only live Pending rows do).
                await db.SeedAsync(TestData.Booking(
                    Guid.NewGuid(), listingId, RenterId,
                    new DateOnly(2031, 1, 1), new DateOnly(2031, 1, 3), status,
                    expiresAt: DateTime.UtcNow.AddDays(-1)));
            }
        }
    }

    // The overlap check of the existing create path runs under SERIALIZABLE. On a Bookings table of a dozen
    // rows the optimiser scans the clustered index, which range-locks EVERY key, so concurrent creates on
    // different listings deadlock (SQL 1205) on the existing overlap-check/insert alone, before the counter
    // is touched (verified with the counter call disabled). A realistic table size makes it seek
    // IX_Bookings_ListingId_Status_StartDate_EndDate instead, so the racers only contend on the counter
    // cell, which is what this test is about. The filler rows are not part of the count.
    private static async Task SeedFillerBookingsAsync(SqlServerTestDatabase db)
    {
        var fillerListingId = Guid.NewGuid();
        await db.SeedAsync(TestData.Listing(fillerListingId, OwnerId, CategoryId));

        await using var context = db.CreateContext();
        await context.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO [Bookings] ([Id], [ListingId], [RenterId], [StartDate], [EndDate], [TotalPrice], [Status],
                                    [ExpiresAt], [CreatedAt], [UpdatedAt])
            SELECT NEWID(), {0}, {1}, DATEADD(DAY, n.[N] * 3, '2040-01-01'), DATEADD(DAY, n.[N] * 3 + 1, '2040-01-01'),
                   100, 1, SYSUTCDATETIME(), SYSUTCDATETIME(), SYSUTCDATETIME()
            FROM (SELECT TOP (3000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS [N]
                  FROM sys.all_objects a CROSS JOIN sys.all_objects b) AS n;
            UPDATE STATISTICS [Bookings] WITH FULLSCAN;
            """,
            fillerListingId, RenterId);
    }

    private static Booking NewBooking(Guid listingId)
    {
        var booking = TestData.Booking(
            Guid.NewGuid(), listingId, RenterId,
            new DateOnly(2030, 6, 1), new DateOnly(2030, 6, 3), BookingStatus.Pending);
        booking.CreatedAt = CreatedAt;
        booking.UpdatedAt = CreatedAt;
        return booking;
    }

    private static int? SqlNumberOf(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqlException sql)
            {
                return sql.Number;
            }
        }

        return null;
    }
}
