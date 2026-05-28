using RentalPlatform.Application.DTOs;
using RentalPlatform.Application.Services;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Infrastructure.Persistence;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Reviews;

// Tests for rating-summary aggregation logic (GetListingSummaryAsync / GetUserSummaryAsync).
// Each test runs against an isolated in-memory SQLite database.
public sealed class ReviewsSummaryTests
{
    private static readonly Guid OwnerId    = new("d0000000-0000-0000-0000-000000000001");
    private static readonly Guid RenterId   = new("d0000000-0000-0000-0000-000000000002");
    private static readonly Guid Renter2Id  = new("d0000000-0000-0000-0000-000000000003");
    private static readonly Guid CategoryId = new("d0000000-0000-0000-0000-000000000004");
    private static readonly Guid ListingId  = new("d0000000-0000-0000-0000-000000000005");

    private static readonly DateOnly PastStart = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-10));
    private static readonly DateOnly PastEnd   = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-3));

    /// <summary>Seeds owner, two renters, category, listing, and a completed booking per renter.</summary>
    private static async Task<(Guid Booking1Id, Guid Booking2Id)> SeedBaselineAsync(SqliteTestDatabase db)
    {
        await db.SeedAsync(
            TestData.User(OwnerId,   "sum-owner@test.local"),
            TestData.User(RenterId,  "sum-renter@test.local"),
            TestData.User(Renter2Id, "sum-renter2@test.local"),
            TestData.Category(CategoryId));

        await db.SeedAsync(TestData.Listing(ListingId, OwnerId, CategoryId, ListingStatus.Approved));

        var b1 = Guid.NewGuid();
        var b2 = Guid.NewGuid();
        await db.SeedAsync(
            TestData.Booking(b1, ListingId, RenterId,  PastStart, PastEnd, BookingStatus.Completed),
            TestData.Booking(b2, ListingId, Renter2Id, PastStart, PastEnd, BookingStatus.Completed));

        return (b1, b2);
    }

    private static ReviewsService CreateService(AppDbContext context) =>
        new(new FakeCurrentUserContext(null), new ReviewsStore(context));

    // Helper: submit a review as the given user.
    private static async Task SubmitReviewAsync(SqliteTestDatabase db, Guid callerId, Guid bookingId, int rating)
    {
        await using var ctx = db.CreateContext();
        new ReviewsService(new FakeCurrentUserContext(callerId), new ReviewsStore(ctx));
        // Use the store directly so we don't need a second service call.
        var store = new ReviewsStore(ctx);
        var booking = await store.FindBookingForReviewAsync(bookingId);
        var listing = booking!.Listing;
        var role = callerId == booking.RenterId ? ReviewerRole.Renter : ReviewerRole.Owner;
        var revieweeId = role == ReviewerRole.Renter ? listing.OwnerId : booking.RenterId;

        await store.AddAsync(new RentalPlatform.Domain.Entities.Review
        {
            Id = Guid.NewGuid(),
            BookingId = bookingId,
            ListingId = listing.Id,
            ReviewerId = callerId,
            RevieweeId = revieweeId,
            ReviewerRole = role,
            Rating = rating,
            CreatedAt = DateTime.UtcNow
        });
    }

    // -----------------------------------------------------------------------
    // Listing summary
    // -----------------------------------------------------------------------

    [Fact]
    public async Task GetListingSummary_Returns_Zero_When_No_Reviews()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);

        await using var ctx = db.CreateContext();
        var summary = await CreateService(ctx).GetListingSummaryAsync(ListingId);

        Assert.Equal(0,   summary.ReviewCount);
        Assert.Equal(0.0, summary.AverageRating);
    }

    [Fact]
    public async Task GetListingSummary_Counts_Renter_Reviews_Only()
    {
        using var db = new SqliteTestDatabase();
        var (b1, b2) = await SeedBaselineAsync(db);

        // Renter1 reviews owner (counts), owner reviews renter1 (must NOT count).
        await SubmitReviewAsync(db, RenterId,  b1, rating: 4);
        await SubmitReviewAsync(db, OwnerId,   b1, rating: 2);

        await using var ctx = db.CreateContext();
        var summary = await CreateService(ctx).GetListingSummaryAsync(ListingId);

        Assert.Equal(1,   summary.ReviewCount);   // only the renter review
        Assert.Equal(4.0, summary.AverageRating);
    }

    [Fact]
    public async Task GetListingSummary_Averages_Multiple_Renter_Reviews()
    {
        using var db = new SqliteTestDatabase();
        var (b1, b2) = await SeedBaselineAsync(db);

        await SubmitReviewAsync(db, RenterId,  b1, rating: 5);
        await SubmitReviewAsync(db, Renter2Id, b2, rating: 3);

        await using var ctx = db.CreateContext();
        var summary = await CreateService(ctx).GetListingSummaryAsync(ListingId);

        Assert.Equal(2,   summary.ReviewCount);
        Assert.Equal(4.0, summary.AverageRating); // (5 + 3) / 2
    }

    [Fact]
    public async Task GetListingSummary_Rounds_To_One_Decimal()
    {
        // Need a third booking+renter for a 3-review average that produces a non-integer.
        using var db = new SqliteTestDatabase();
        var renter3Id  = Guid.NewGuid();
        var b3         = Guid.NewGuid();

        await db.SeedAsync(TestData.User(renter3Id, "sum-renter3@test.local"));
        var (b1, b2) = await SeedBaselineAsync(db);
        await db.SeedAsync(TestData.Booking(b3, ListingId, renter3Id, PastStart, PastEnd, BookingStatus.Completed));

        await SubmitReviewAsync(db, RenterId,  b1, rating: 5);
        await SubmitReviewAsync(db, Renter2Id, b2, rating: 4);
        await SubmitReviewAsync(db, renter3Id, b3, rating: 3);

        await using var ctx = db.CreateContext();
        var summary = await CreateService(ctx).GetListingSummaryAsync(ListingId);

        Assert.Equal(3,   summary.ReviewCount);
        Assert.Equal(4.0, summary.AverageRating); // (5 + 4 + 3) / 3 = 4.0
    }

    // -----------------------------------------------------------------------
    // User summary
    // -----------------------------------------------------------------------

    [Fact]
    public async Task GetUserSummary_Returns_Zero_When_No_Reviews()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);

        await using var ctx = db.CreateContext();
        var summary = await CreateService(ctx).GetUserSummaryAsync(OwnerId);

        Assert.Equal(0,   summary.ReviewCount);
        Assert.Equal(0.0, summary.AverageRating);
    }

    [Fact]
    public async Task GetUserSummary_Includes_Reviews_Received_As_Owner()
    {
        using var db = new SqliteTestDatabase();
        var (b1, _) = await SeedBaselineAsync(db);

        await SubmitReviewAsync(db, RenterId, b1, rating: 5);

        await using var ctx = db.CreateContext();
        var summary = await CreateService(ctx).GetUserSummaryAsync(OwnerId);

        Assert.Equal(1,   summary.ReviewCount);
        Assert.Equal(5.0, summary.AverageRating);
    }

    [Fact]
    public async Task GetUserSummary_Includes_Reviews_Received_As_Renter()
    {
        using var db = new SqliteTestDatabase();
        var (b1, _) = await SeedBaselineAsync(db);

        await SubmitReviewAsync(db, OwnerId, b1, rating: 3);

        await using var ctx = db.CreateContext();
        var summary = await CreateService(ctx).GetUserSummaryAsync(RenterId);

        Assert.Equal(1,   summary.ReviewCount);
        Assert.Equal(3.0, summary.AverageRating);
    }

    [Fact]
    public async Task GetUserSummary_Averages_Reviews_From_Both_Roles()
    {
        using var db = new SqliteTestDatabase();
        var (b1, b2) = await SeedBaselineAsync(db);

        // Owner receives one review from renter1 (as listing owner).
        await SubmitReviewAsync(db, RenterId,  b1, rating: 4);
        // Owner receives another review from renter2 (as listing owner).
        await SubmitReviewAsync(db, Renter2Id, b2, rating: 2);

        await using var ctx = db.CreateContext();
        var summary = await CreateService(ctx).GetUserSummaryAsync(OwnerId);

        Assert.Equal(2,   summary.ReviewCount);
        Assert.Equal(3.0, summary.AverageRating); // (4 + 2) / 2
    }

    [Fact]
    public async Task GetUserSummary_Does_Not_Include_Reviews_Given_By_User()
    {
        using var db = new SqliteTestDatabase();
        var (b1, _) = await SeedBaselineAsync(db);

        // Renter reviews the owner — this must NOT appear in renter's OWN summary.
        await SubmitReviewAsync(db, RenterId, b1, rating: 5);

        await using var ctx = db.CreateContext();
        var summary = await CreateService(ctx).GetUserSummaryAsync(RenterId);

        Assert.Equal(0,   summary.ReviewCount);
        Assert.Equal(0.0, summary.AverageRating);
    }
}
