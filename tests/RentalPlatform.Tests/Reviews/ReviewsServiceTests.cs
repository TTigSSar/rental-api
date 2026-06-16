using RentalPlatform.Application.DTOs;
using RentalPlatform.Application.Services;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Infrastructure.Persistence;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Reviews;

// Service-layer tests for the three-table review model. Each test runs against an
// isolated in-memory SQLite database.
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
        await db.SeedAsync(TestData.Booking(bookingId, ListingId, RenterId, PastStart, PastEnd, status));
        return bookingId;
    }

    private static ReviewsService CreateService(AppDbContext context, Guid? callerId) =>
        new(new FakeCurrentUserContext(callerId), new ReviewsStore(context));

    private static CreateToyReviewRequest ToyRequest(Guid bookingId, int overall = 5, string? comment = null) => new()
    {
        BookingId = bookingId,
        OverallRating = overall,
        ConditionRating = overall,
        CleanlinessRating = overall,
        ValueForMoneyRating = overall,
        FunPlayValueRating = overall,
        DescriptionAccuracyRating = overall,
        Comment = comment
    };

    private static CreateOwnerReviewRequest OwnerRequest(Guid bookingId, int score = 5, string? comment = null) => new()
    {
        BookingId = bookingId,
        CommunicationRating = score,
        PickupHandoverRating = score,
        FriendlinessRating = score,
        Comment = comment
    };

    private static CreateRenterReviewRequest RenterRequest(Guid bookingId, int score = 5, string? comment = null) => new()
    {
        BookingId = bookingId,
        CommunicationRating = score,
        ReturnedOnTimeRating = score,
        CareOfToyRating = score,
        WouldRentAgainRating = score,
        Comment = comment
    };

    // --- toy review submission ---

    [Fact]
    public async Task SubmitToy_Succeeds_For_Renter()
    {
        using var db = new SqliteTestDatabase();
        var bookingId = await SeedBaselineAsync(db);

        await using var ctx = db.CreateContext();
        var result = await CreateService(ctx, RenterId).SubmitToyReviewAsync(ToyRequest(bookingId, 5, "Loved it"));

        Assert.True(result.IsSuccess);
        Assert.Equal("renter", result.Value!.Role);
        Assert.True(result.Value.HasToyReview);
        Assert.False(result.Value.CanReviewToy);   // already submitted
        Assert.True(result.Value.CanReviewOwner);  // owner still pending
    }

    [Fact]
    public async Task SubmitToy_Fails_For_Owner()
    {
        using var db = new SqliteTestDatabase();
        var bookingId = await SeedBaselineAsync(db);

        await using var ctx = db.CreateContext();
        var result = await CreateService(ctx, OwnerId).SubmitToyReviewAsync(ToyRequest(bookingId));

        Assert.False(result.IsSuccess);
        Assert.Equal("review.forbidden", result.Error!.Code);
    }

    [Fact]
    public async Task SubmitToy_Fails_For_Stranger()
    {
        using var db = new SqliteTestDatabase();
        var bookingId = await SeedBaselineAsync(db);

        await using var ctx = db.CreateContext();
        var result = await CreateService(ctx, StrangerId).SubmitToyReviewAsync(ToyRequest(bookingId));

        Assert.False(result.IsSuccess);
        Assert.Equal("review.forbidden", result.Error!.Code);
    }

    [Fact]
    public async Task SubmitToy_Fails_When_Not_Authenticated()
    {
        using var db = new SqliteTestDatabase();
        var bookingId = await SeedBaselineAsync(db);

        await using var ctx = db.CreateContext();
        var result = await CreateService(ctx, null).SubmitToyReviewAsync(ToyRequest(bookingId));

        Assert.False(result.IsSuccess);
        Assert.Equal("review.unauthenticated", result.Error!.Code);
    }

    [Theory]
    [InlineData(BookingStatus.Pending)]
    [InlineData(BookingStatus.Approved)]
    [InlineData(BookingStatus.Rejected)]
    [InlineData(BookingStatus.Cancelled)]
    [InlineData(BookingStatus.Expired)]
    public async Task SubmitToy_Fails_For_Non_Completed_Booking(BookingStatus status)
    {
        using var db = new SqliteTestDatabase();
        var bookingId = await SeedBaselineAsync(db, status);

        await using var ctx = db.CreateContext();
        var result = await CreateService(ctx, RenterId).SubmitToyReviewAsync(ToyRequest(bookingId));

        Assert.False(result.IsSuccess);
        Assert.Equal("review.booking_not_completed", result.Error!.Code);
    }

    [Fact]
    public async Task SubmitToy_Fails_When_Already_Submitted()
    {
        using var db = new SqliteTestDatabase();
        var bookingId = await SeedBaselineAsync(db);

        await using (var ctx1 = db.CreateContext())
            await CreateService(ctx1, RenterId).SubmitToyReviewAsync(ToyRequest(bookingId));

        await using var ctx2 = db.CreateContext();
        var result = await CreateService(ctx2, RenterId).SubmitToyReviewAsync(ToyRequest(bookingId));

        Assert.False(result.IsSuccess);
        Assert.Equal("review.already_submitted", result.Error!.Code);
    }

    [Fact]
    public async Task SubmitToy_Trims_Comment_And_Treats_Whitespace_As_Null()
    {
        using var db = new SqliteTestDatabase();
        var bookingId = await SeedBaselineAsync(db);

        await using (var ctx1 = db.CreateContext())
            await CreateService(ctx1, RenterId).SubmitToyReviewAsync(ToyRequest(bookingId, 4, "  Nice  "));

        await using var read = db.CreateContext();
        var summary = await CreateService(read, null).GetListingToyReviewsAsync(ListingId);
        Assert.Single(summary.Comments);
        Assert.Equal("Nice", summary.Comments.First().Comment);
    }

    // --- owner & renter review submission ---

    [Fact]
    public async Task SubmitOwner_Succeeds_For_Renter()
    {
        using var db = new SqliteTestDatabase();
        var bookingId = await SeedBaselineAsync(db);

        await using var ctx = db.CreateContext();
        var result = await CreateService(ctx, RenterId).SubmitOwnerReviewAsync(OwnerRequest(bookingId));

        Assert.True(result.IsSuccess);
        Assert.True(result.Value!.HasOwnerReview);
    }

    [Fact]
    public async Task SubmitRenter_Succeeds_For_Owner()
    {
        using var db = new SqliteTestDatabase();
        var bookingId = await SeedBaselineAsync(db);

        await using var ctx = db.CreateContext();
        var result = await CreateService(ctx, OwnerId).SubmitRenterReviewAsync(RenterRequest(bookingId));

        Assert.True(result.IsSuccess);
        Assert.Equal("owner", result.Value!.Role);
        Assert.True(result.Value.HasRenterReview);
    }

    [Fact]
    public async Task SubmitRenter_Fails_For_Renter()
    {
        using var db = new SqliteTestDatabase();
        var bookingId = await SeedBaselineAsync(db);

        await using var ctx = db.CreateContext();
        var result = await CreateService(ctx, RenterId).SubmitRenterReviewAsync(RenterRequest(bookingId));

        Assert.False(result.IsSuccess);
        Assert.Equal("review.forbidden", result.Error!.Code);
    }

    [Fact]
    public async Task Toy_And_Owner_Reviews_Are_Independent()
    {
        using var db = new SqliteTestDatabase();
        var bookingId = await SeedBaselineAsync(db);

        await using (var ctx1 = db.CreateContext())
            await CreateService(ctx1, RenterId).SubmitToyReviewAsync(ToyRequest(bookingId));

        await using var ctx2 = db.CreateContext();
        var status = await CreateService(ctx2, RenterId).GetBookingReviewStatusAsync(bookingId);

        Assert.True(status.Value!.HasToyReview);
        Assert.False(status.Value.HasOwnerReview);
        Assert.True(status.Value.CanReviewOwner);
    }

    // --- aggregates: min-2 threshold ---

    [Fact]
    public async Task ToySummary_Hides_Aggregate_Below_Two_Reviews()
    {
        using var db = new SqliteTestDatabase();
        var bookingId = await SeedBaselineAsync(db);

        await using (var ctx1 = db.CreateContext())
            await CreateService(ctx1, RenterId).SubmitToyReviewAsync(ToyRequest(bookingId, 5, "Solid"));

        await using var read = db.CreateContext();
        var summary = await CreateService(read, null).GetListingToyReviewsAsync(ListingId);

        Assert.Equal(1, summary.ReviewCount);
        Assert.False(summary.HasAggregate);
        Assert.Single(summary.Comments);   // comment still shown
    }

    [Fact]
    public async Task ToySummary_Shows_Aggregate_At_Two_Reviews()
    {
        using var db = new SqliteTestDatabase();
        await db.SeedAsync(
            TestData.User(OwnerId,  "agg-owner@test.local"),
            TestData.User(RenterId, "agg-renter@test.local"),
            TestData.Category(CategoryId));
        await db.SeedAsync(TestData.Listing(ListingId, OwnerId, CategoryId, ListingStatus.Approved));

        var renter2 = Guid.NewGuid();
        await db.SeedAsync(TestData.User(renter2, "agg-renter2@test.local"));

        var b1 = Guid.NewGuid();
        var b2 = Guid.NewGuid();
        await db.SeedAsync(
            TestData.Booking(b1, ListingId, RenterId, PastStart, PastEnd, BookingStatus.Completed),
            TestData.Booking(b2, ListingId, renter2,  PastStart, PastEnd, BookingStatus.Completed));

        await using (var ctx1 = db.CreateContext())
            await CreateService(ctx1, RenterId).SubmitToyReviewAsync(ToyRequest(b1, 5));
        await using (var ctx2 = db.CreateContext())
            await CreateService(ctx2, renter2).SubmitToyReviewAsync(ToyRequest(b2, 3));

        await using var read = db.CreateContext();
        var summary = await CreateService(read, null).GetListingToyReviewsAsync(ListingId);

        Assert.Equal(2, summary.ReviewCount);
        Assert.True(summary.HasAggregate);
        Assert.Equal(4.0, summary.OverallAverage);     // (5 + 3) / 2
        Assert.Equal(1, summary.Distribution[4]);      // one 5★
        Assert.Equal(1, summary.Distribution[2]);      // one 3★
    }

    [Fact]
    public async Task OwnerSummary_Averages_Subscores()
    {
        using var db = new SqliteTestDatabase();
        var bookingId = await SeedBaselineAsync(db);

        await using (var ctx1 = db.CreateContext())
            await CreateService(ctx1, RenterId).SubmitOwnerReviewAsync(OwnerRequest(bookingId, 4, "Friendly"));

        await using var read = db.CreateContext();
        var summary = await CreateService(read, null).GetOwnerReviewsAsync(OwnerId);

        Assert.Equal(1, summary.ReviewCount);
        Assert.Equal(4.0, summary.CommunicationAverage);
        Assert.Equal(4.0, summary.OverallAverage);
        Assert.Single(summary.Comments);
    }

    [Fact]
    public async Task Comment_Carries_RentedDays_From_Booking()
    {
        using var db = new SqliteTestDatabase();
        var bookingId = await SeedBaselineAsync(db);

        await using (var ctx1 = db.CreateContext())
            await CreateService(ctx1, RenterId).SubmitToyReviewAsync(ToyRequest(bookingId, 5, "Great"));

        await using var read = db.CreateContext();
        var summary = await CreateService(read, null).GetListingToyReviewsAsync(ListingId);

        // PastEnd - PastStart inclusive = 8 days.
        Assert.Equal(PastEnd.DayNumber - PastStart.DayNumber + 1, summary.Comments.First().RentedDays);
    }
}
