using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RentalPlatform.Application.DTOs;
using RentalPlatform.Application.Services;
using RentalPlatform.Domain;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Infrastructure.Persistence;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Bookings;

// ADR-043: rental days = max(1, end - start); a booking occupies nights [start, effEnd);
// back-to-back is allowed; minimum rental is enforced on create.
public sealed class RentalPeriodTests
{
    private static readonly Guid OwnerId = new("a1000000-0000-0000-0000-000000000001");
    private static readonly Guid RenterId = new("a1000000-0000-0000-0000-000000000002");
    private static readonly Guid OtherUserId = new("a1000000-0000-0000-0000-000000000003");
    private static readonly Guid CategoryId = new("a1000000-0000-0000-0000-000000000004");
    private static readonly Guid ListingId = new("a1000000-0000-0000-0000-000000000005");

    private static readonly DateOnly Today = TestData.Today;

    // --- Helper ---

    [Theory]
    [InlineData("2030-03-10", "2030-03-10", 1)] // same day
    [InlineData("2030-03-10", "2030-03-11", 1)] // +1
    [InlineData("2030-03-10", "2030-03-17", 7)] // +7
    [InlineData("2030-01-30", "2030-02-02", 3)] // month crossing
    [InlineData("2030-12-30", "2031-01-02", 3)] // year crossing
    public void Days_Follows_ADR043(string start, string end, int expected)
    {
        Assert.Equal(expected, RentalPeriod.Days(DateOnly.Parse(start), DateOnly.Parse(end)));
    }

    [Fact]
    public void EffectiveEnd_Is_End_Or_Start_Plus_One_For_Same_Day()
    {
        var d = new DateOnly(2030, 3, 13);
        Assert.Equal(d.AddDays(1), RentalPeriod.EffectiveEnd(d, d));
        Assert.Equal(d.AddDays(5), RentalPeriod.EffectiveEnd(d, d.AddDays(5)));
    }

    [Theory]
    [InlineData(10, 13, 13, 15, false)] // A returns 13, B picks up 13
    [InlineData(13, 15, 10, 13, false)] // mirror
    [InlineData(10, 14, 13, 15, true)]  // true overlap
    [InlineData(13, 13, 13, 13, true)]  // same-day vs same-day, same date
    [InlineData(13, 13, 10, 13, false)] // same-day on another booking's return day
    [InlineData(10, 13, 13, 13, false)] // mirror
    [InlineData(13, 13, 10, 14, true)]  // same-day inside another booking's nights
    [InlineData(13, 13, 13, 15, true)]  // same-day on another booking's pickup night
    public void Overlaps_Follows_ADR043(int sa, int ea, int sb, int eb, bool expected)
    {
        var b = new DateOnly(2030, 3, 1);
        Assert.Equal(expected, RentalPeriod.Overlaps(b.AddDays(sa), b.AddDays(ea), b.AddDays(sb), b.AddDays(eb)));
    }

    // --- Service (SQLite) ---

    private static async Task SeedAsync(SqliteTestDatabase db, int? minRentalDays = null)
    {
        await db.SeedAsync(
            TestData.User(OwnerId, "owner@test.local"),
            TestData.User(RenterId, "renter@test.local"),
            TestData.User(OtherUserId, "other@test.local"),
            TestData.Category(CategoryId));

        var listing = TestData.Listing(ListingId, OwnerId, CategoryId, ListingStatus.Approved);
        listing.MinRentalDays = minRentalDays;
        await db.SeedAsync(listing);
    }

    private static BookingsService CreateService(AppDbContext context, Guid currentUserId) =>
        new(
            new FakeCurrentUserContext(currentUserId),
            new BookingsStore(context),
            new FakeNotificationEmitter(),
            new FakeChatSystemMessageEmitter(),
            new ReviewsStore(context),
            new ConversationsStore(context, NullLogger<ConversationsStore>.Instance));

    private static CreateBookingRequest Request(int startOffset, int endOffset) => new()
    {
        ListingId = ListingId,
        StartDate = Today.AddDays(startOffset),
        EndDate = Today.AddDays(endOffset)
    };

    private static async Task SeedBookingAsync(
        SqliteTestDatabase db, int startOffset, int endOffset, BookingStatus status, Guid? id = null) =>
        await db.SeedAsync(TestData.Booking(
            id ?? Guid.NewGuid(), ListingId, OtherUserId,
            Today.AddDays(startOffset), Today.AddDays(endOffset), status));

    [Theory]
    [InlineData(5, 5, 1)]
    [InlineData(5, 6, 1)]
    [InlineData(5, 12, 7)]
    public async Task Create_TotalPrice_Uses_Rental_Days(int startOffset, int endOffset, int expectedDays)
    {
        using var db = new SqliteTestDatabase();
        await SeedAsync(db);

        await using var context = db.CreateContext();
        var result = await CreateService(context, RenterId).CreateAsync(Request(startOffset, endOffset));

        Assert.True(result.IsSuccess);
        Assert.Equal(expectedDays * 10m, result.Value!.TotalPrice); // TestData price is 10/day
    }

    [Fact]
    public async Task Create_TotalPrice_Across_Month_Boundary()
    {
        using var db = new SqliteTestDatabase();
        await SeedAsync(db);

        // Pick a start inside the next 40 days that is 2 days before a month end.
        var monthEnd = new DateOnly(Today.Year, Today.Month, 1).AddMonths(2).AddDays(-1);
        var start = monthEnd.AddDays(-1);
        var end = start.AddDays(3);

        await using var context = db.CreateContext();
        var result = await CreateService(context, RenterId).CreateAsync(new CreateBookingRequest
        {
            ListingId = ListingId,
            StartDate = start,
            EndDate = end
        });

        Assert.True(result.IsSuccess);
        Assert.Equal(30m, result.Value!.TotalPrice);
    }

    [Theory]
    [InlineData(1, 5, 5, true)]   // same day vs min 1
    [InlineData(1, 5, 6, true)]
    [InlineData(7, 5, 11, false)] // 6 days vs min 7
    [InlineData(7, 5, 12, true)]  // 7 days vs min 7
    [InlineData(7, 5, 5, false)]  // same day counts 1 day
    [InlineData(0, 5, 5, true)]   // 0 = no minimum
    public async Task Create_Enforces_Minimum_Rental(int min, int startOffset, int endOffset, bool allowed)
    {
        using var db = new SqliteTestDatabase();
        await SeedAsync(db, min);

        await using var context = db.CreateContext();
        var result = await CreateService(context, RenterId).CreateAsync(Request(startOffset, endOffset));

        Assert.Equal(allowed, result.IsSuccess);
        if (!allowed)
        {
            Assert.Equal("booking.below_min_rental", result.Error!.Code);
            Assert.Contains($"{min} days", result.Error.Message);
        }
    }

    [Fact]
    public async Task Create_Allows_Exactly_Max_Rental_Days()
    {
        using var db = new SqliteTestDatabase();
        await SeedAsync(db);

        await using var context = db.CreateContext();
        var result = await CreateService(context, RenterId).CreateAsync(Request(1, 91)); // 90 days

        Assert.True(result.IsSuccess);
    }

    [Theory]
    [InlineData(5, 8, 8, 10, true)]   // back-to-back: existing returns 8, new picks up 8
    [InlineData(5, 8, 3, 5, true)]    // new ends on existing's start day
    [InlineData(5, 8, 7, 9, false)]   // true overlap
    [InlineData(5, 8, 6, 6, false)]   // same-day inside existing nights
    [InlineData(5, 8, 8, 8, true)]    // same-day on existing's return day
    [InlineData(6, 6, 6, 6, false)]   // same-day vs same-day, same date
    [InlineData(6, 6, 5, 6, true)]    // new returns on the same-day booking's date... night 5 only
    [InlineData(6, 6, 6, 8, false)]   // new picks up on same-day booking's night
    public async Task Create_Overlap_Follows_ADR043(int es, int ee, int ns, int ne, bool allowed)
    {
        foreach (var status in new[] { BookingStatus.Approved, BookingStatus.Pending })
        {
            using var db = new SqliteTestDatabase();
            await SeedAsync(db);
            await SeedBookingAsync(db, es, ee, status);

            await using var context = db.CreateContext();
            var result = await CreateService(context, RenterId).CreateAsync(Request(ns, ne));

            Assert.Equal(allowed, result.IsSuccess);
            if (!allowed)
            {
                Assert.Equal("booking.overlap", result.Error!.Code);
            }
        }
    }

    [Theory]
    [InlineData(5, 8, 8, 10, true)]
    [InlineData(5, 8, 7, 9, false)]
    [InlineData(6, 6, 6, 6, false)]
    [InlineData(5, 8, 8, 8, true)]
    public async Task Approve_Overlap_Follows_ADR043(int es, int ee, int ps, int pe, bool allowed)
    {
        using var db = new SqliteTestDatabase();
        await SeedAsync(db);
        await SeedBookingAsync(db, es, ee, BookingStatus.Approved);
        var pendingId = Guid.NewGuid();
        await db.SeedAsync(TestData.Booking(
            pendingId, ListingId, RenterId, Today.AddDays(ps), Today.AddDays(pe),
            BookingStatus.Pending, expiresAt: DateTime.UtcNow.AddHours(24)));

        await using var context = db.CreateContext();
        var result = await CreateService(context, OwnerId).ApproveAsync(pendingId);

        Assert.Equal(allowed, result.IsSuccess);
        if (!allowed)
        {
            Assert.Equal("booking.overlap", result.Error!.Code);
        }
    }
}
