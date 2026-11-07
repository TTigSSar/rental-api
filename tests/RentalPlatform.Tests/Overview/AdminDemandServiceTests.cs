using RentalPlatform.Application.Services;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Infrastructure.Persistence;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Overview;

// P3-3 GET /api/admin/demand at the service level (plan section 4: #9, #10, #11).
public sealed class AdminDemandServiceTests
{
    private static readonly Guid AdminId = new("f4000000-0000-0000-0000-000000000001");
    private static readonly Guid UserId = new("f4000000-0000-0000-0000-000000000002");

    private static async Task SeedUsersAsync(SqliteTestDatabase db) =>
        await db.SeedAsync(
            TestData.User(AdminId, "admin@demand-svc.local", role: UserRole.Admin),
            TestData.User(UserId, "user@demand-svc.local"));

    private static AdminDemandService Service(
        SqliteTestDatabase db, out Microsoft.EntityFrameworkCore.DbContext context, Guid? userId = null, FakeTimeProvider? clock = null)
    {
        var ctx = db.CreateContext();
        context = ctx;
        return new AdminDemandService(
            new FakeCurrentUserContext(userId ?? AdminId),
            new AdminDemandStore(ctx),
            clock ?? new FakeTimeProvider(new DateTimeOffset(2026, 11, 7, 10, 0, 0, TimeSpan.Zero)));
    }

    private static DemandDailyCount Row(string day, Guid? district, Guid category, int count) => new()
    {
        Id = Guid.NewGuid(), Day = DateOnly.ParseExact(day, "yyyy-MM-dd"), DistrictId = district, CategoryId = category, BookingRequests = count
    };

    [Fact]
    public async Task Test9_An_Empty_Range_Returns_An_Empty_Rows_List_And_Echoes_The_Range()
    {
        using var db = new SqliteTestDatabase();
        await SeedUsersAsync(db);
        var service = Service(db, out var context);
        await using var _ = context;

        var result = await service.GetDemandAsync("2026-01-01", "2026-01-31");

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value!.Rows);
        Assert.Equal(new DateOnly(2026, 1, 1), result.Value.From);
        Assert.Equal(new DateOnly(2026, 1, 31), result.Value.To);
    }

    [Fact]
    public async Task Test10_Both_Bounds_Are_Inclusive()
    {
        using var db = new SqliteTestDatabase();
        await SeedUsersAsync(db);
        var category = Guid.NewGuid();
        await db.SeedAsync(
            Row("2026-02-28", null, category, 1),
            Row("2026-03-01", null, category, 2),
            Row("2026-03-05", null, category, 3),
            Row("2026-03-06", null, category, 4));
        var service = Service(db, out var context);
        await using var _ = context;

        var result = await service.GetDemandAsync("2026-03-01", "2026-03-05");

        Assert.Equal(new[] { 2, 3 }, result.Value!.Rows.Select(row => row.BookingRequests));
    }

    [Fact]
    public async Task Test10_Without_Bounds_The_Range_Is_Today_In_Yerevan_And_The_29_Days_Before()
    {
        using var db = new SqliteTestDatabase();
        await SeedUsersAsync(db);

        // 2026-11-07 20:30Z is already 2026-11-08 in Yerevan.
        var service = Service(db, out var context, clock: new FakeTimeProvider(new DateTimeOffset(2026, 11, 7, 20, 30, 0, TimeSpan.Zero)));
        await using var _ = context;

        var result = await service.GetDemandAsync(null, "  ");

        Assert.True(result.IsSuccess);
        Assert.Equal(new DateOnly(2026, 11, 8), result.Value!.To);
        Assert.Equal(new DateOnly(2026, 10, 10), result.Value.From);
        Assert.Equal(30, result.Value.To.DayNumber - result.Value.From.DayNumber + 1);
    }

    [Theory]
    [InlineData("2026-03-01", null)]
    [InlineData(null, "2026-03-01")]
    [InlineData("2026-03-01", "")]
    [InlineData("01/03/2026", "2026-03-05")]
    [InlineData("2026-3-1", "2026-03-05")]
    [InlineData("2026-03-01T00:00:00", "2026-03-05")]
    [InlineData("nope", "2026-03-05")]
    [InlineData("2026-03-06", "2026-03-05")]
    [InlineData("2025-01-01", "2026-01-02")] // 367 days inclusive
    public async Task Test10_Invalid_Ranges_Are_Refused_With_The_Range_Error_Code(string? from, string? to)
    {
        using var db = new SqliteTestDatabase();
        await SeedUsersAsync(db);
        var service = Service(db, out var context);
        await using var _ = context;

        var result = await service.GetDemandAsync(from, to);

        Assert.False(result.IsSuccess);
        Assert.Equal("admin.demand_range_invalid", result.Error!.Code);
    }

    [Fact]
    public async Task Test10_A_Range_Of_Exactly_366_Days_Is_Accepted_Across_A_Leap_Year()
    {
        using var db = new SqliteTestDatabase();
        await SeedUsersAsync(db);
        var service = Service(db, out var context);
        await using var _ = context;

        var ok = await service.GetDemandAsync("2023-12-31", "2024-12-30");   // 366 inclusive (2024 is a leap year)
        var tooLong = await service.GetDemandAsync("2023-12-31", "2024-12-31"); // 367 inclusive

        Assert.True(ok.IsSuccess);
        Assert.Equal("admin.demand_range_invalid", tooLong.Error!.Code);
    }

    [Fact]
    public async Task Test11_Rows_Are_Ordered_By_Day_Then_District_With_Null_First_Then_Category()
    {
        using var db = new SqliteTestDatabase();
        await SeedUsersAsync(db);
        var districtA = new Guid("00000000-0000-0000-0000-00000000000a");
        var districtB = new Guid("00000000-0000-0000-0000-00000000000b");
        var catA = new Guid("00000000-0000-0000-0000-0000000000a1");
        var catB = new Guid("00000000-0000-0000-0000-0000000000b1");
        await db.SeedAsync(
            Row("2026-03-02", districtA, catA, 1),
            Row("2026-03-01", districtB, catB, 2),
            Row("2026-03-01", districtB, catA, 3),
            Row("2026-03-01", districtA, catB, 4),
            Row("2026-03-01", null, catB, 5),
            Row("2026-03-01", null, catA, 6));
        var service = Service(db, out var context);
        await using var _ = context;

        var result = await service.GetDemandAsync("2026-03-01", "2026-03-02");

        Assert.Equal(new[] { 6, 5, 4, 3, 2, 1 }, result.Value!.Rows.Select(row => row.BookingRequests));
    }

    [Fact]
    public async Task A_Non_Admin_Is_Forbidden_And_An_Anonymous_Caller_Is_Unauthenticated()
    {
        using var db = new SqliteTestDatabase();
        await SeedUsersAsync(db);

        var forbidden = Service(db, out var c1, userId: UserId);
        await using var _1 = c1;
        Assert.Equal("admin.forbidden", (await forbidden.GetDemandAsync(null, null)).Error!.Code);

        var anonymous = new AdminDemandService(new FakeCurrentUserContext(null), new AdminDemandStore(db.CreateContext()), new FakeTimeProvider());
        Assert.Equal("admin.unauthenticated", (await anonymous.GetDemandAsync(null, null)).Error!.Code);
    }
}
