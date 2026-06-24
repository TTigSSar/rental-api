using RentalPlatform.Application.DTOs;
using RentalPlatform.Application.Services;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Infrastructure.Persistence;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Bookings;

// Booking lifecycle coverage. Each test runs the real BookingsService over the real
// BookingsStore against an isolated in-memory SQLite database.
public sealed class BookingsServiceTests
{
    private static readonly Guid OwnerId = new("a0000000-0000-0000-0000-000000000001");
    private static readonly Guid RenterId = new("a0000000-0000-0000-0000-000000000002");
    private static readonly Guid OtherUserId = new("a0000000-0000-0000-0000-000000000003");
    private static readonly Guid CategoryId = new("a0000000-0000-0000-0000-000000000004");
    private static readonly Guid ListingId = new("a0000000-0000-0000-0000-000000000005");

    private static readonly DateOnly Today = TestData.Today;

    // Seeds the baseline graph: owner, renter, a third user, a category and an approved listing.
    private static async Task SeedBaselineAsync(SqliteTestDatabase db)
    {
        await db.SeedAsync(
            TestData.User(OwnerId, "owner@test.local"),
            TestData.User(RenterId, "renter@test.local"),
            TestData.User(OtherUserId, "other@test.local"),
            TestData.Category(CategoryId));

        await db.SeedAsync(TestData.Listing(ListingId, OwnerId, CategoryId, ListingStatus.Approved));
    }

    private static BookingsService CreateService(AppDbContext context, Guid currentUserId) =>
        new(new FakeCurrentUserContext(currentUserId), new BookingsStore(context));

    [Fact]
    public async Task Create_Rejects_Overlap_With_Pending_Booking()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);
        await db.SeedAsync(TestData.Booking(
            Guid.NewGuid(), ListingId, OtherUserId,
            Today.AddDays(5), Today.AddDays(8),
            BookingStatus.Pending,
            expiresAt: DateTime.UtcNow.AddHours(24)));

        await using var context = db.CreateContext();
        var service = CreateService(context, RenterId);

        var result = await service.CreateAsync(new CreateBookingRequest
        {
            ListingId = ListingId,
            StartDate = Today.AddDays(6),
            EndDate = Today.AddDays(7)
        });

        Assert.False(result.IsSuccess);
        Assert.Equal("booking.overlap", result.Error!.Code);
    }

    [Fact]
    public async Task Create_Rejects_Overlap_With_Approved_Booking()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);
        await db.SeedAsync(TestData.Booking(
            Guid.NewGuid(), ListingId, OtherUserId,
            Today.AddDays(5), Today.AddDays(8),
            BookingStatus.Approved));

        await using var context = db.CreateContext();
        var service = CreateService(context, RenterId);

        var result = await service.CreateAsync(new CreateBookingRequest
        {
            ListingId = ListingId,
            StartDate = Today.AddDays(6),
            EndDate = Today.AddDays(7)
        });

        Assert.False(result.IsSuccess);
        Assert.Equal("booking.overlap", result.Error!.Code);
    }

    [Theory]
    [InlineData(BookingStatus.Expired)]
    [InlineData(BookingStatus.Rejected)]
    [InlineData(BookingStatus.Cancelled)]
    public async Task Create_Succeeds_When_Existing_Booking_Is_Inactive(BookingStatus inactiveStatus)
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);
        // Same dates as the new request — only the inactive status keeps it from blocking.
        await db.SeedAsync(TestData.Booking(
            Guid.NewGuid(), ListingId, OtherUserId,
            Today.AddDays(6), Today.AddDays(7),
            inactiveStatus,
            expiresAt: DateTime.UtcNow.AddHours(-1)));

        await using var context = db.CreateContext();
        var service = CreateService(context, RenterId);

        var result = await service.CreateAsync(new CreateBookingRequest
        {
            ListingId = ListingId,
            StartDate = Today.AddDays(6),
            EndDate = Today.AddDays(7)
        });

        Assert.True(result.IsSuccess);
        Assert.Equal(BookingStatus.Pending, result.Value!.Status);
    }

    [Fact]
    public async Task Approve_Succeeds_For_Pending_Booking_By_Owner()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);
        var bookingId = Guid.NewGuid();
        await db.SeedAsync(TestData.Booking(
            bookingId, ListingId, RenterId,
            Today.AddDays(5), Today.AddDays(8),
            BookingStatus.Pending,
            expiresAt: DateTime.UtcNow.AddHours(24)));

        await using var context = db.CreateContext();
        var service = CreateService(context, OwnerId);

        var result = await service.ApproveAsync(bookingId);

        Assert.True(result.IsSuccess);
        Assert.Equal(BookingStatus.Approved, result.Value!.Status);
    }

    [Theory]
    [InlineData(BookingStatus.Rejected)]
    [InlineData(BookingStatus.Cancelled)]
    [InlineData(BookingStatus.Expired)]
    [InlineData(BookingStatus.Approved)]
    public async Task Approve_Rejects_Non_Pending_Booking(BookingStatus currentStatus)
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);
        var bookingId = Guid.NewGuid();
        await db.SeedAsync(TestData.Booking(
            bookingId, ListingId, RenterId,
            Today.AddDays(5), Today.AddDays(8),
            currentStatus,
            expiresAt: DateTime.UtcNow.AddHours(24)));

        await using var context = db.CreateContext();
        var service = CreateService(context, OwnerId);

        var result = await service.ApproveAsync(bookingId);

        Assert.False(result.IsSuccess);
        Assert.Equal("booking.not_pending", result.Error!.Code);
    }

    [Fact]
    public async Task Cancel_Succeeds_For_Pending_Booking_By_Renter()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);
        var bookingId = Guid.NewGuid();
        await db.SeedAsync(TestData.Booking(
            bookingId, ListingId, RenterId,
            Today.AddDays(5), Today.AddDays(8),
            BookingStatus.Pending,
            expiresAt: DateTime.UtcNow.AddHours(24)));

        await using var context = db.CreateContext();
        var service = CreateService(context, RenterId);

        var result = await service.CancelAsync(bookingId);

        Assert.True(result.IsSuccess);
        Assert.Equal(BookingStatus.Cancelled, result.Value!.Status);
    }

    [Fact]
    public async Task Cancel_Succeeds_For_Approved_Booking_Before_Start_Date()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);
        var bookingId = Guid.NewGuid();
        await db.SeedAsync(TestData.Booking(
            bookingId, ListingId, RenterId,
            Today.AddDays(5), Today.AddDays(8),
            BookingStatus.Approved));

        await using var context = db.CreateContext();
        var service = CreateService(context, RenterId);

        var result = await service.CancelAsync(bookingId);

        Assert.True(result.IsSuccess);
        Assert.Equal(BookingStatus.Cancelled, result.Value!.Status);
    }

    [Fact]
    public async Task Cancel_Rejects_Approved_Booking_On_Or_After_Start_Date()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);
        var bookingId = Guid.NewGuid();
        // Start date is today — the rental window has begun, so cancellation is blocked.
        await db.SeedAsync(TestData.Booking(
            bookingId, ListingId, RenterId,
            Today, Today.AddDays(3),
            BookingStatus.Approved));

        await using var context = db.CreateContext();
        var service = CreateService(context, RenterId);

        var result = await service.CancelAsync(bookingId);

        Assert.False(result.IsSuccess);
        Assert.Equal("booking.not_cancellable", result.Error!.Code);
    }

    [Fact]
    public async Task Approve_Rejected_When_Caller_Is_Not_Listing_Owner()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);
        var bookingId = Guid.NewGuid();
        await db.SeedAsync(TestData.Booking(
            bookingId, ListingId, RenterId,
            Today.AddDays(5), Today.AddDays(8),
            BookingStatus.Pending,
            expiresAt: DateTime.UtcNow.AddHours(24)));

        await using var context = db.CreateContext();
        // The renter is a valid user but does not own the listing.
        var service = CreateService(context, RenterId);

        var result = await service.ApproveAsync(bookingId);

        Assert.False(result.IsSuccess);
        Assert.Equal("booking.forbidden", result.Error!.Code);
    }

    [Fact]
    public async Task Reject_Rejected_When_Caller_Is_Not_Listing_Owner()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);
        var bookingId = Guid.NewGuid();
        await db.SeedAsync(TestData.Booking(
            bookingId, ListingId, RenterId,
            Today.AddDays(5), Today.AddDays(8),
            BookingStatus.Pending,
            expiresAt: DateTime.UtcNow.AddHours(24)));

        await using var context = db.CreateContext();
        var service = CreateService(context, OtherUserId);

        var result = await service.RejectAsync(bookingId);

        Assert.False(result.IsSuccess);
        Assert.Equal("booking.forbidden", result.Error!.Code);
    }

    // --- Lifecycle: owner activates (hand-over) then completes the rental ---

    private async Task<Guid> SeedApprovedReturnableAsync(SqliteTestDatabase db)
    {
        var bookingId = Guid.NewGuid();
        await db.SeedAsync(TestData.Booking(
            bookingId, ListingId, RenterId,
            Today.AddDays(-5), Today.AddDays(-1),
            BookingStatus.Approved));
        return bookingId;
    }

    [Fact]
    public async Task MarkActive_By_Owner_Sets_Active()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);
        var bookingId = await SeedApprovedReturnableAsync(db);

        await using var context = db.CreateContext();
        var result = await CreateService(context, OwnerId).MarkActiveAsync(bookingId);

        Assert.True(result.IsSuccess);
        Assert.Equal(BookingStatus.Active, result.Value!.Status);
    }

    [Fact]
    public async Task MarkActive_By_Renter_Is_Forbidden()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);
        var bookingId = await SeedApprovedReturnableAsync(db);

        await using var context = db.CreateContext();
        var result = await CreateService(context, RenterId).MarkActiveAsync(bookingId);

        Assert.False(result.IsSuccess);
        Assert.Equal("booking.owner_only", result.Error!.Code);
    }

    [Fact]
    public async Task MarkActive_By_Stranger_Is_Forbidden()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);
        var bookingId = await SeedApprovedReturnableAsync(db);

        await using var context = db.CreateContext();
        var result = await CreateService(context, OtherUserId).MarkActiveAsync(bookingId);

        Assert.False(result.IsSuccess);
        Assert.Equal("booking.forbidden", result.Error!.Code);
    }

    [Theory]
    [InlineData(BookingStatus.Pending)]
    [InlineData(BookingStatus.Completed)]
    [InlineData(BookingStatus.Cancelled)]
    public async Task MarkActive_Fails_When_Not_Approved(BookingStatus status)
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);
        var bookingId = Guid.NewGuid();
        await db.SeedAsync(TestData.Booking(
            bookingId, ListingId, RenterId,
            Today.AddDays(-5), Today.AddDays(-1),
            status,
            expiresAt: DateTime.UtcNow.AddHours(-24)));

        await using var context = db.CreateContext();
        var result = await CreateService(context, OwnerId).MarkActiveAsync(bookingId);

        Assert.False(result.IsSuccess);
        Assert.Equal("booking.not_activatable", result.Error!.Code);
    }

    [Fact]
    public async Task Complete_By_Owner_Completes_Active_Booking()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);
        var bookingId = await SeedApprovedReturnableAsync(db);

        await using (var activateContext = db.CreateContext())
        {
            await CreateService(activateContext, OwnerId).MarkActiveAsync(bookingId);
        }

        await using var context = db.CreateContext();
        var result = await CreateService(context, OwnerId).CompleteAsync(bookingId);

        Assert.True(result.IsSuccess);
        Assert.Equal(BookingStatus.Completed, result.Value!.Status);
    }

    [Fact]
    public async Task Complete_By_Renter_Is_Forbidden()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);
        var bookingId = await SeedApprovedReturnableAsync(db);

        await using (var activateContext = db.CreateContext())
        {
            await CreateService(activateContext, OwnerId).MarkActiveAsync(bookingId);
        }

        await using var context = db.CreateContext();
        var result = await CreateService(context, RenterId).CompleteAsync(bookingId);

        Assert.False(result.IsSuccess);
        Assert.Equal("booking.owner_only", result.Error!.Code);
    }

    [Fact]
    public async Task Complete_Fails_When_Not_Active()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);
        var bookingId = await SeedApprovedReturnableAsync(db);

        await using var context = db.CreateContext();
        var result = await CreateService(context, OwnerId).CompleteAsync(bookingId);

        Assert.False(result.IsSuccess);
        Assert.Equal("booking.not_completable", result.Error!.Code);
    }

    [Theory]
    [InlineData(true)]  // owner
    [InlineData(false)] // renter
    public async Task GetById_Returns_Detail_For_Both_Parties(bool asOwner)
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);
        var bookingId = await SeedApprovedReturnableAsync(db);

        await using var context = db.CreateContext();
        var result = await CreateService(context, asOwner ? OwnerId : RenterId).GetByIdAsync(bookingId);

        Assert.True(result.IsSuccess);
        Assert.Equal(asOwner ? "owner" : "renter", result.Value!.Role);
        // Counterparty is the other side of the booking.
        Assert.Equal(asOwner ? RenterId : OwnerId, result.Value!.CounterpartyId);
    }

    [Fact]
    public async Task Reject_With_Reason_Is_Exposed_On_Detail()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);
        var bookingId = Guid.NewGuid();
        await db.SeedAsync(TestData.Booking(
            bookingId, ListingId, RenterId,
            Today.AddDays(5), Today.AddDays(8),
            BookingStatus.Pending,
            expiresAt: DateTime.UtcNow.AddHours(24)));

        await using (var rejectContext = db.CreateContext())
        {
            var result = await CreateService(rejectContext, OwnerId)
                .RejectAsync(bookingId, "dates_unavailable");
            Assert.True(result.IsSuccess);
        }

        await using var context = db.CreateContext();
        var detail = await CreateService(context, RenterId).GetByIdAsync(bookingId);

        Assert.True(detail.IsSuccess);
        Assert.Equal(BookingStatus.Rejected, detail.Value!.Status);
        Assert.Equal("dates_unavailable", detail.Value!.RejectionReason);
    }

    [Fact]
    public async Task GetById_For_Stranger_Is_Forbidden()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);
        var bookingId = await SeedApprovedReturnableAsync(db);

        await using var context = db.CreateContext();
        var result = await CreateService(context, OtherUserId).GetByIdAsync(bookingId);

        Assert.False(result.IsSuccess);
        Assert.Equal("booking.forbidden", result.Error!.Code);
    }

    [Theory]
    [InlineData(BookingStatus.Rejected)]
    [InlineData(BookingStatus.Cancelled)]
    [InlineData(BookingStatus.Expired)]
    public async Task GetMine_Does_Not_Change_Non_Approved_Status_For_Past_Booking(BookingStatus seedStatus)
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);
        var bookingId = Guid.NewGuid();

        await db.SeedAsync(TestData.Booking(
            bookingId, ListingId, RenterId,
            Today.AddDays(-5), Today.AddDays(-1),
            seedStatus,
            expiresAt: DateTime.UtcNow.AddHours(-24)));

        await using var context = db.CreateContext();
        var service = CreateService(context, RenterId);

        var result = await service.GetMineAsync();

        Assert.True(result.IsSuccess);
        var booking = Assert.Single(result.Value!, b => b.Id == bookingId);
        Assert.Equal(seedStatus, booking.Status);
    }
}
