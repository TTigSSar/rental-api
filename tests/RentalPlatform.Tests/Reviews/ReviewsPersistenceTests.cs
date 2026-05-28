using Microsoft.EntityFrameworkCore;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Infrastructure.Persistence;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Reviews;

// Persistence-layer tests for the Review entity. Each test runs against an isolated
// in-memory SQLite database — no shared state between tests.
public sealed class ReviewsPersistenceTests
{
    private static readonly Guid OwnerId    = new("b0000000-0000-0000-0000-000000000001");
    private static readonly Guid RenterId   = new("b0000000-0000-0000-0000-000000000002");
    private static readonly Guid CategoryId = new("b0000000-0000-0000-0000-000000000003");
    private static readonly Guid ListingId  = new("b0000000-0000-0000-0000-000000000004");
    private static readonly DateOnly Past   = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-10));

    private static async Task<Guid> SeedBaselineAsync(SqliteTestDatabase db)
    {
        await db.SeedAsync(
            TestData.User(OwnerId,  "review-owner@test.local"),
            TestData.User(RenterId, "review-renter@test.local"),
            TestData.Category(CategoryId));

        await db.SeedAsync(TestData.Listing(ListingId, OwnerId, CategoryId, ListingStatus.Approved));

        var bookingId = Guid.NewGuid();
        await db.SeedAsync(TestData.Booking(
            bookingId, ListingId, RenterId,
            Past.AddDays(-5), Past,
            BookingStatus.Completed));

        return bookingId;
    }

    private static Review MakeReview(
        Guid bookingId,
        ReviewerRole role,
        int rating = 5,
        string? comment = null) => new()
    {
        Id = Guid.NewGuid(),
        BookingId = bookingId,
        ListingId = ListingId,
        ReviewerId = role == ReviewerRole.Renter ? RenterId : OwnerId,
        RevieweeId = role == ReviewerRole.Renter ? OwnerId  : RenterId,
        ReviewerRole = role,
        Rating = rating,
        Comment = comment,
        CreatedAt = DateTime.UtcNow
    };

    private static ReviewsStore CreateStore(AppDbContext context) => new(context);

    // -------------------------------------------------------------------------
    // Persist renter review
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Can_Persist_Renter_Review()
    {
        using var db = new SqliteTestDatabase();
        var bookingId = await SeedBaselineAsync(db);

        await using var context = db.CreateContext();
        var store = CreateStore(context);

        var review = MakeReview(bookingId, ReviewerRole.Renter, rating: 4, comment: "Great toy!");
        await store.AddAsync(review);

        await using var verify = db.CreateContext();
        var saved = await verify.Reviews.FirstOrDefaultAsync(r => r.Id == review.Id);
        Assert.NotNull(saved);
        Assert.Equal(ReviewerRole.Renter, saved.ReviewerRole);
        Assert.Equal(4, saved.Rating);
        Assert.Equal("Great toy!", saved.Comment);
    }

    // -------------------------------------------------------------------------
    // Persist owner review
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Can_Persist_Owner_Review()
    {
        using var db = new SqliteTestDatabase();
        var bookingId = await SeedBaselineAsync(db);

        await using var context = db.CreateContext();
        var store = CreateStore(context);

        var review = MakeReview(bookingId, ReviewerRole.Owner, rating: 3);
        await store.AddAsync(review);

        await using var verify = db.CreateContext();
        var saved = await verify.Reviews.FirstOrDefaultAsync(r => r.Id == review.Id);
        Assert.NotNull(saved);
        Assert.Equal(ReviewerRole.Owner, saved.ReviewerRole);
        Assert.Equal(3, saved.Rating);
    }

    // -------------------------------------------------------------------------
    // Both sides can submit reviews for the same booking (renter + owner)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Both_Sides_Can_Review_Same_Booking()
    {
        using var db = new SqliteTestDatabase();
        var bookingId = await SeedBaselineAsync(db);

        await using var context = db.CreateContext();
        var store = CreateStore(context);

        await store.AddAsync(MakeReview(bookingId, ReviewerRole.Renter, rating: 5));

        await using var context2 = db.CreateContext();
        var store2 = CreateStore(context2);
        await store2.AddAsync(MakeReview(bookingId, ReviewerRole.Owner, rating: 4));

        await using var verify = db.CreateContext();
        var count = await verify.Reviews.CountAsync(r => r.BookingId == bookingId);
        Assert.Equal(2, count);
    }

    // -------------------------------------------------------------------------
    // Duplicate: same BookingId + ReviewerRole must be rejected
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Duplicate_Review_For_Same_Booking_And_Role_Throws()
    {
        using var db = new SqliteTestDatabase();
        var bookingId = await SeedBaselineAsync(db);

        await using var firstContext = db.CreateContext();
        await CreateStore(firstContext).AddAsync(MakeReview(bookingId, ReviewerRole.Renter));

        await using var secondContext = db.CreateContext();
        var duplicate = MakeReview(bookingId, ReviewerRole.Renter, rating: 2);

        // The unique index on (BookingId, ReviewerRole) must reject the second insert.
        await Assert.ThrowsAnyAsync<DbUpdateException>(
            () => CreateStore(secondContext).AddAsync(duplicate));
    }

    // -------------------------------------------------------------------------
    // HasReview checks
    // -------------------------------------------------------------------------

    [Fact]
    public async Task HasReview_Returns_False_When_No_Review_Exists()
    {
        using var db = new SqliteTestDatabase();
        var bookingId = await SeedBaselineAsync(db);

        await using var context = db.CreateContext();
        var result = await CreateStore(context)
            .HasReviewForBookingAsync(bookingId, ReviewerRole.Renter);

        Assert.False(result);
    }

    [Fact]
    public async Task HasReview_Returns_True_After_Persist()
    {
        using var db = new SqliteTestDatabase();
        var bookingId = await SeedBaselineAsync(db);

        await using var writeContext = db.CreateContext();
        await CreateStore(writeContext).AddAsync(MakeReview(bookingId, ReviewerRole.Renter));

        await using var readContext = db.CreateContext();
        var result = await CreateStore(readContext)
            .HasReviewForBookingAsync(bookingId, ReviewerRole.Renter);

        Assert.True(result);
    }

    [Fact]
    public async Task HasReview_Returns_False_For_Other_Role_After_Renter_Persists()
    {
        using var db = new SqliteTestDatabase();
        var bookingId = await SeedBaselineAsync(db);

        await using var writeContext = db.CreateContext();
        await CreateStore(writeContext).AddAsync(MakeReview(bookingId, ReviewerRole.Renter));

        await using var readContext = db.CreateContext();
        // Owner has NOT reviewed yet — must be false.
        var result = await CreateStore(readContext)
            .HasReviewForBookingAsync(bookingId, ReviewerRole.Owner);

        Assert.False(result);
    }

    // -------------------------------------------------------------------------
    // Rating check constraint: 1–5 only
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(6)]
    [InlineData(99)]
    public async Task Rating_Outside_1_To_5_Violates_Constraint(int invalidRating)
    {
        using var db = new SqliteTestDatabase();
        var bookingId = await SeedBaselineAsync(db);

        await using var context = db.CreateContext();
        var review = MakeReview(bookingId, ReviewerRole.Renter, rating: invalidRating);

        await Assert.ThrowsAnyAsync<DbUpdateException>(
            () => CreateStore(context).AddAsync(review));
    }

    // -------------------------------------------------------------------------
    // GetByListing / GetByUser
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetByListing_Returns_Only_Renter_Reviews_For_That_Listing()
    {
        using var db = new SqliteTestDatabase();
        var bookingId = await SeedBaselineAsync(db);

        // Persist both a renter and an owner review.
        await using var ctx1 = db.CreateContext();
        await CreateStore(ctx1).AddAsync(MakeReview(bookingId, ReviewerRole.Renter, rating: 5));
        await using var ctx2 = db.CreateContext();
        await CreateStore(ctx2).AddAsync(MakeReview(bookingId, ReviewerRole.Owner, rating: 4));

        await using var readContext = db.CreateContext();
        var results = await CreateStore(readContext).GetByListingAsync(ListingId);

        // GetByListing scopes to Renter reviews only (shown on listing detail page).
        Assert.Single(results);
        Assert.Equal(ReviewerRole.Renter, results.First().ReviewerRole);
    }

    [Fact]
    public async Task GetByUser_Returns_All_Reviews_Received_By_That_User()
    {
        using var db = new SqliteTestDatabase();
        var bookingId = await SeedBaselineAsync(db);

        await using var ctx = db.CreateContext();
        await CreateStore(ctx).AddAsync(MakeReview(bookingId, ReviewerRole.Renter, rating: 5));

        await using var readContext = db.CreateContext();
        // Renter reviewed the owner — so owner is the reviewee.
        var results = await CreateStore(readContext).GetByUserAsync(OwnerId);

        Assert.Single(results);
        Assert.Equal(OwnerId, results.First().RevieweeId);
    }
}
