using RentalPlatform.Domain.Enums;
using RentalPlatform.Infrastructure.Persistence;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Infrastructure;

// ADR-043 overlap predicate against a REAL SQL Server. EF Core 3+ throws on client evaluation of a
// Where/Any predicate, so a passing run proves the DateOnly.AddDays / conditional expression
// translates to T-SQL (DATEADD / CASE) and that the SQL semantics match RentalPeriod.
public sealed class BookingsStoreOverlapSqlServerTests
{
    private static readonly Guid OwnerId = new("e3000000-0000-0000-0000-000000000001");
    private static readonly Guid RenterId = new("e3000000-0000-0000-0000-000000000002");
    private static readonly Guid CategoryId = new("e3000000-0000-0000-0000-000000000003");
    private static readonly Guid ListingId = new("e3000000-0000-0000-0000-000000000004");

    private static readonly DateOnly Base = new(2030, 3, 1);

    private static async Task SeedAsync(SqlServerTestDatabase db, int existingStart, int existingEnd)
    {
        await db.SeedAsync(
            TestData.User(OwnerId, "owner@test.local"),
            TestData.User(RenterId, "renter@test.local"),
            TestData.Category(CategoryId));
        await db.SeedAsync(TestData.Listing(ListingId, OwnerId, CategoryId));
        await db.SeedAsync(TestData.Booking(
            Guid.NewGuid(), ListingId, RenterId,
            Base.AddDays(existingStart), Base.AddDays(existingEnd), BookingStatus.Approved));
    }

    public static IEnumerable<object[]> Cases() => new[]
    {
        new object[] { 10, 13, 13, 15, true },  // back-to-back
        new object[] { 10, 13, 8, 10, true },   // ends on existing start day
        new object[] { 10, 13, 12, 14, false }, // true overlap
        new object[] { 10, 13, 13, 13, true },  // same-day on return day
        new object[] { 10, 13, 11, 11, false }, // same-day inside nights
        new object[] { 13, 13, 13, 13, false }, // same-day vs same-day
        new object[] { 13, 13, 12, 13, true },  // returns on same-day booking's date
        new object[] { 13, 13, 13, 15, false }, // picks up on same-day booking's night
        new object[] { 31, 33, 28, 35, false }, // spans across a month boundary
    };

    [SqlServerFact]
    public async Task TryCreate_Translates_And_Follows_ADR043()
    {
        foreach (var c in Cases())
        {
            using var db = new SqlServerTestDatabase();
            await SeedAsync(db, (int)c[0], (int)c[1]);

            await using var context = db.CreateContext();
            var added = await new BookingsStore(context).TryCreateBookingAsync(TestData.Booking(
                Guid.NewGuid(), ListingId, RenterId,
                Base.AddDays((int)c[2]), Base.AddDays((int)c[3]), BookingStatus.Pending));

            Assert.True((bool)c[4] == added, $"existing [{c[0]},{c[1]}] vs new [{c[2]},{c[3]}]");
        }
    }

    [SqlServerFact]
    public async Task TryApprove_Translates_And_Follows_ADR043()
    {
        foreach (var c in Cases())
        {
            using var db = new SqlServerTestDatabase();
            await SeedAsync(db, (int)c[0], (int)c[1]);

            var pending = TestData.Booking(
                Guid.NewGuid(), ListingId, RenterId,
                Base.AddDays((int)c[2]), Base.AddDays((int)c[3]), BookingStatus.Pending);
            await db.SeedAsync(pending);

            await using var context = db.CreateContext();
            var tracked = await context.Bookings.FindAsync(pending.Id);
            var approved = await new BookingsStore(context).TryApproveBookingAsync(tracked!);

            Assert.True((bool)c[4] == approved, $"existing [{c[0]},{c[1]}] vs pending [{c[2]},{c[3]}]");
        }
    }
}
