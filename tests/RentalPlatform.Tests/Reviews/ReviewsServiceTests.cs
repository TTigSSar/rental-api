using RentalPlatform.Application.DTOs;
using RentalPlatform.Application.Services;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Infrastructure.Persistence;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Reviews;

// Service-layer tests for ReviewsService. Each test runs against an isolated
// in-memory SQLite database so no shared state exists between tests.
public sealed class ReviewsServiceTests
{
    private static readonly Guid OwnerId    = new("c0000000-0000-0000-0000-000000000001");
    private static readonly Guid RenterId   = new("c0000000-0000-0000-0000-000000000002");
    private static readonly Guid StrangerId = new("c0000000-0000-0000-0000-000000000003");
    private static readonly Guid CategoryId = new("c0000000-0000-0000-0000-000000000004");
    private static readonly Guid ListingId  = new("c0000000-0000-0000-0000-000000000005");

    private static readonly DateOnly PastStart = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-10));
    private static readonly DateOnly PastEnd   = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-3));

    private static async Task<Guid> SeedBaselineAsync(SqliteTestDatabase db, BookingStatus status = BookingStatus.Completed)
    {
        await db.SeedAsync(
            TestData.User(OwnerId,    "srv-review-owner@test.local"),
            TestData.User(RenterId,   "srv-review-renter@test.local"),
            TestData.User(StrangerId, "srv-review-stranger@test.local"),
            TestData.Category(CategoryId));

        await db.SeedAsync(TestData.Listing(ListingId, OwnerId, CategoryId, ListingStatus.Approved));

        var bookingId = Guid.NewGuid();
        await db.SeedAsync(TestData.Booking(
            bookingId, ListingId, RenterId,
            PastStart, PastEnd,
            status));

        return bookingId;
    }

    private static ReviewsService CreateService(AppDbContext context, Guid? callerId) =>
        new(new FakeCurrentUserContext(callerId), new ReviewsStore(context));

    // -----------------------------------------------------------------------
    // Successful submissions
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Create_Succeeds_For_Renter_Reviewing_Owner()
    {
        using var db = new SqliteTestDatabase();
        var bookingId = await SeedBaselineAsync(db);

        await using var context = db.CreateContext();
        var service = CreateService(context, RenterId);

        var result = await service.CreateAsync(new CreateReviewRequest
        {
            BookingId = bookingId,
            Rating = 5,
            Comment = "Great owner!"
        });

        Assert.True(result.IsSuccess);
        Assert.Equal(ReviewerRole.Renter, result.Value!.ReviewerRole);
        Assert.Equal(OwnerId,             result.Value.RevieweeId);
        Assert.Equal(RenterId,            result.Value.ReviewerId);
        Assert.Equal(5,                   result.Value.Rating);
        Assert.Equal("Great owner!",      result.Value.Comment);
    }

    [Fact]
    public async Task Create_Succeeds_For_Owner_Reviewing_Renter()
    {
        using var db = new SqliteTestDatabase();
        var bookingId = await SeedBaselineAsync(db);

        await using var context = db.CreateContext();
        var service = CreateService(context, OwnerId);

        var result = await service.CreateAsync(new CreateReviewRequest
        {
            BookingId = bookingId,
            Rating = 4
        });

        Assert.True(result.IsSuccess);
        Assert.Equal(ReviewerRole.Owner, result.Value!.ReviewerRole);
        Assert.Equal(RenterId,           result.Value.RevieweeId);
        Assert.Equal(OwnerId,            result.Value.ReviewerId);
        Assert.Equal(4,                  result.Value.Rating);
        Assert.Null(result.Value.Comment);
    }

    [Fact]
    public async Task Create_Trims_Comment_Whitespace()
    {
        using var db = new SqliteTestDatabase();
        var bookingId = await SeedBaselineAsync(db);

        await using var context = db.CreateContext();
        var result = await CreateService(context, RenterId).CreateAsync(new CreateReviewRequest
        {
            BookingId = bookingId,
            Rating = 3,
            Comment = "  Nice!  "
        });

        Assert.True(result.IsSuccess);
        Assert.Equal("Nice!", result.Value!.Comment);
    }

    [Fact]
    public async Task Create_Treats_Whitespace_Only_Comment_As_Null()
    {
        using var db = new SqliteTestDatabase();
        var bookingId = await SeedBaselineAsync(db);

        await using var context = db.CreateContext();
        var result = await CreateService(context, RenterId).CreateAsync(new CreateReviewRequest
        {
            BookingId = bookingId,
            Rating = 3,
            Comment = "   "
        });

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value!.Comment);
    }

    // -----------------------------------------------------------------------
    // Booking status validation
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(BookingStatus.Pending)]
    [InlineData(BookingStatus.Approved)]
    [InlineData(BookingStatus.Rejected)]
    [InlineData(BookingStatus.Cancelled)]
    [InlineData(BookingStatus.Expired)]
    public async Task Create_Fails_For_Non_Completed_Booking(BookingStatus status)
    {
        using var db = new SqliteTestDatabase();
        var bookingId = await SeedBaselineAsync(db, status);

        await using var context = db.CreateContext();
        var result = await CreateService(context, RenterId).CreateAsync(new CreateReviewRequest
        {
            BookingId = bookingId,
            Rating = 5
        });

        Assert.False(result.IsSuccess);
        Assert.Equal("review.booking_not_completed", result.Error!.Code);
    }

    // -----------------------------------------------------------------------
    // Booking not found
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Create_Fails_When_Booking_Does_Not_Exist()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);

        await using var context = db.CreateContext();
        var result = await CreateService(context, RenterId).CreateAsync(new CreateReviewRequest
        {
            BookingId = Guid.NewGuid(),
            Rating = 5
        });

        Assert.False(result.IsSuccess);
        Assert.Equal("review.booking_not_found", result.Error!.Code);
    }

    // -----------------------------------------------------------------------
    // Authorization: caller must be renter or owner of this booking
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Create_Fails_For_Unrelated_User()
    {
        using var db = new SqliteTestDatabase();
        var bookingId = await SeedBaselineAsync(db);

        await using var context = db.CreateContext();
        var result = await CreateService(context, StrangerId).CreateAsync(new CreateReviewRequest
        {
            BookingId = bookingId,
            Rating = 5
        });

        Assert.False(result.IsSuccess);
        Assert.Equal("review.forbidden", result.Error!.Code);
    }

    [Fact]
    public async Task Create_Fails_When_Not_Authenticated()
    {
        using var db = new SqliteTestDatabase();
        var bookingId = await SeedBaselineAsync(db);

        await using var context = db.CreateContext();
        var result = await CreateService(context, null).CreateAsync(new CreateReviewRequest
        {
            BookingId = bookingId,
            Rating = 5
        });

        Assert.False(result.IsSuccess);
        Assert.Equal("review.unauthenticated", result.Error!.Code);
    }

    // -----------------------------------------------------------------------
    // Duplicate prevention
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Create_Fails_When_Renter_Already_Reviewed()
    {
        using var db = new SqliteTestDatabase();
        var bookingId = await SeedBaselineAsync(db);

        await using var first = db.CreateContext();
        await CreateService(first, RenterId).CreateAsync(new CreateReviewRequest { BookingId = bookingId, Rating = 5 });

        await using var second = db.CreateContext();
        var result = await CreateService(second, RenterId).CreateAsync(new CreateReviewRequest
        {
            BookingId = bookingId,
            Rating = 3
        });

        Assert.False(result.IsSuccess);
        Assert.Equal("review.already_submitted", result.Error!.Code);
    }

    [Fact]
    public async Task Create_Allows_Both_Sides_To_Review_Independently()
    {
        using var db = new SqliteTestDatabase();
        var bookingId = await SeedBaselineAsync(db);

        await using var ctx1 = db.CreateContext();
        var renterResult = await CreateService(ctx1, RenterId).CreateAsync(
            new CreateReviewRequest { BookingId = bookingId, Rating = 5 });

        await using var ctx2 = db.CreateContext();
        var ownerResult = await CreateService(ctx2, OwnerId).CreateAsync(
            new CreateReviewRequest { BookingId = bookingId, Rating = 4 });

        Assert.True(renterResult.IsSuccess);
        Assert.True(ownerResult.IsSuccess);
    }

    // -----------------------------------------------------------------------
    // Rating validation
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(6)]
    [InlineData(100)]
    public async Task Create_Fails_For_Invalid_Rating(int rating)
    {
        using var db = new SqliteTestDatabase();
        var bookingId = await SeedBaselineAsync(db);

        await using var context = db.CreateContext();
        var result = await CreateService(context, RenterId).CreateAsync(new CreateReviewRequest
        {
            BookingId = bookingId,
            Rating = rating
        });

        Assert.False(result.IsSuccess);
        Assert.Equal("review.invalid_rating", result.Error!.Code);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    public async Task Create_Accepts_Valid_Rating_Boundaries(int rating)
    {
        using var db = new SqliteTestDatabase();
        var bookingId = await SeedBaselineAsync(db);

        await using var context = db.CreateContext();
        var result = await CreateService(context, RenterId).CreateAsync(new CreateReviewRequest
        {
            BookingId = bookingId,
            Rating = rating
        });

        Assert.True(result.IsSuccess);
        Assert.Equal(rating, result.Value!.Rating);
    }

    // -----------------------------------------------------------------------
    // Read queries
    // -----------------------------------------------------------------------

    [Fact]
    public async Task GetByListing_Returns_Reviews_For_Listing()
    {
        using var db = new SqliteTestDatabase();
        var bookingId = await SeedBaselineAsync(db);

        await using var writeCtx = db.CreateContext();
        await CreateService(writeCtx, RenterId).CreateAsync(
            new CreateReviewRequest { BookingId = bookingId, Rating = 4, Comment = "Good!" });

        await using var readCtx = db.CreateContext();
        var result = await CreateService(readCtx, null).GetByListingAsync(ListingId);

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value!);
        Assert.Equal(4, result.Value!.First().Rating);
        Assert.Equal("Good!", result.Value!.First().Comment);
    }

    [Fact]
    public async Task GetByListing_Returns_Empty_When_No_Reviews()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);

        await using var context = db.CreateContext();
        var result = await CreateService(context, null).GetByListingAsync(ListingId);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value!);
    }

    [Fact]
    public async Task GetByUser_Returns_Reviews_Received_By_User()
    {
        using var db = new SqliteTestDatabase();
        var bookingId = await SeedBaselineAsync(db);

        // Renter reviews the owner → owner is the reviewee.
        await using var writeCtx = db.CreateContext();
        await CreateService(writeCtx, RenterId).CreateAsync(
            new CreateReviewRequest { BookingId = bookingId, Rating = 5 });

        await using var readCtx = db.CreateContext();
        var result = await CreateService(readCtx, null).GetByUserAsync(OwnerId);

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value!);
        Assert.Equal(OwnerId, result.Value!.First().RevieweeId);
    }
}
