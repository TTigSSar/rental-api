using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Api;

// HTTP contract tests for ReviewsController (three-table model). Covers routing, auth,
// status-code mapping, and response shape — not business logic (that lives in ReviewsServiceTests).
[Collection("Integration")]
public sealed class ReviewsHttpTests
{
    private static readonly DateOnly PastStart = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-10));
    private static readonly DateOnly PastEnd   = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-3));

    private readonly RentalPlatformWebAppFactory _factory;

    public ReviewsHttpTests(RentalPlatformWebAppFactory factory) => _factory = factory;

    private async Task<(Guid OwnerId, Guid RenterId, Guid ListingId, Guid BookingId)> SeedCompletedBookingAsync()
    {
        var ownerId    = Guid.NewGuid();
        var renterId   = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var listingId  = Guid.NewGuid();
        var bookingId  = Guid.NewGuid();

        await _factory.SeedAsync(
            TestData.User(ownerId,  $"{ownerId:N}@rev-owner.local"),
            TestData.User(renterId, $"{renterId:N}@rev-renter.local"),
            TestData.Category(categoryId));
        await _factory.SeedAsync(TestData.Listing(listingId, ownerId, categoryId, ListingStatus.Approved));
        await _factory.SeedAsync(TestData.Booking(bookingId, listingId, renterId, PastStart, PastEnd, BookingStatus.Completed));

        return (ownerId, renterId, listingId, bookingId);
    }

    private HttpClient ClientFor(Guid userId, string email)
    {
        var token = TestJwtTokenHelper.GenerateToken(userId, email);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static object ToyBody(Guid bookingId, int overall = 5, string? comment = null) => new
    {
        bookingId,
        overallRating = overall,
        conditionRating = overall,
        cleanlinessRating = overall,
        valueForMoneyRating = overall,
        funPlayValueRating = overall,
        descriptionAccuracyRating = overall,
        comment
    };

    private static object OwnerBody(Guid bookingId, int score = 5) => new
    {
        bookingId,
        communicationRating = score,
        pickupHandoverRating = score,
        friendlinessRating = score
    };

    private static object RenterBody(Guid bookingId, int score = 5) => new
    {
        bookingId,
        communicationRating = score,
        returnedOnTimeRating = score,
        careOfToyRating = score,
        wouldRentAgainRating = score
    };

    [Fact]
    public async Task SubmitToy_Returns_201_For_Renter()
    {
        var (_, renterId, _, bookingId) = await SeedCompletedBookingAsync();
        var client = ClientFor(renterId, $"{renterId:N}@rev-renter.local");

        var response = await client.PostAsJsonAsync("/api/reviews/toy", ToyBody(bookingId, 5, "Great!"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("hasToyReview").GetBoolean());
        Assert.Equal("renter", doc.RootElement.GetProperty("role").GetString());
    }

    [Fact]
    public async Task SubmitRenter_Returns_201_For_Owner()
    {
        var (ownerId, _, _, bookingId) = await SeedCompletedBookingAsync();
        var client = ClientFor(ownerId, $"{ownerId:N}@rev-owner.local");

        var response = await client.PostAsJsonAsync("/api/reviews/renter", RenterBody(bookingId));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("hasRenterReview").GetBoolean());
    }

    [Fact]
    public async Task SubmitToy_Returns_401_When_Anonymous()
    {
        var (_, _, _, bookingId) = await SeedCompletedBookingAsync();

        var response = await _factory.CreateClient().PostAsJsonAsync("/api/reviews/toy", ToyBody(bookingId));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task SubmitToy_Returns_403_For_Stranger()
    {
        var (_, _, _, bookingId) = await SeedCompletedBookingAsync();
        var strangerId = Guid.NewGuid();
        await _factory.SeedAsync(TestData.User(strangerId, $"{strangerId:N}@stranger.local"));
        var client = ClientFor(strangerId, $"{strangerId:N}@stranger.local");

        var response = await client.PostAsJsonAsync("/api/reviews/toy", ToyBody(bookingId));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task SubmitToy_Returns_409_For_Duplicate()
    {
        var (_, renterId, _, bookingId) = await SeedCompletedBookingAsync();
        var client = ClientFor(renterId, $"{renterId:N}@rev-renter.local");

        await client.PostAsJsonAsync("/api/reviews/toy", ToyBody(bookingId, 5));
        var response = await client.PostAsJsonAsync("/api/reviews/toy", ToyBody(bookingId, 3));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("urn:rental:error:review.already_submitted", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("review.already_submitted", doc.RootElement.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task SubmitToy_Returns_400_For_Invalid_Rating()
    {
        var (_, renterId, _, bookingId) = await SeedCompletedBookingAsync();
        var client = ClientFor(renterId, $"{renterId:N}@rev-renter.local");

        var response = await client.PostAsJsonAsync("/api/reviews/toy", ToyBody(bookingId, 6));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetBookingStatus_Returns_Flags_For_Renter()
    {
        var (_, renterId, _, bookingId) = await SeedCompletedBookingAsync();
        var client = ClientFor(renterId, $"{renterId:N}@rev-renter.local");

        var response = await client.GetAsync($"/api/reviews/booking/{bookingId}/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("canReviewToy").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("canReviewOwner").GetBoolean());
        Assert.False(doc.RootElement.GetProperty("canReviewRenter").GetBoolean());
    }

    [Fact]
    public async Task GetListingToyReviews_Is_Public_And_Has_No_Scores_In_Comments()
    {
        var (_, renterId, listingId, bookingId) = await SeedCompletedBookingAsync();
        var client = ClientFor(renterId, $"{renterId:N}@rev-renter.local");
        await client.PostAsJsonAsync("/api/reviews/toy", ToyBody(bookingId, 5, "Spotless and complete"));

        var response = await _factory.CreateClient().GetAsync($"/api/reviews/listing/{listingId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(1, doc.RootElement.GetProperty("reviewCount").GetInt32());
        Assert.False(doc.RootElement.GetProperty("hasAggregate").GetBoolean()); // below min 2
        var comment = doc.RootElement.GetProperty("comments").EnumerateArray().First();
        Assert.Equal("Spotless and complete", comment.GetProperty("comment").GetString());
        // Comment cards must NOT leak any per-review score.
        Assert.False(comment.TryGetProperty("rating", out _));
        Assert.False(comment.TryGetProperty("overallRating", out _));
    }

    [Fact]
    public async Task GetOwnerReviews_Is_Public()
    {
        var (ownerId, _, _, _) = await SeedCompletedBookingAsync();

        var response = await _factory.CreateClient().GetAsync($"/api/reviews/owner/{ownerId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(0, doc.RootElement.GetProperty("reviewCount").GetInt32());
    }
}
