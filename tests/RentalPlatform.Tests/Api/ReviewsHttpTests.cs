using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Api;

// HTTP contract tests for ReviewsController. Covers routing, auth, status-code mapping,
// and response shape — not business logic (that belongs in ReviewsServiceTests).
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

        await _factory.SeedAsync(
            TestData.Listing(listingId, ownerId, categoryId, ListingStatus.Approved));

        await _factory.SeedAsync(
            TestData.Booking(bookingId, listingId, renterId, PastStart, PastEnd, BookingStatus.Completed));

        return (ownerId, renterId, listingId, bookingId);
    }

    [Fact]
    public async Task Create_Returns_201_For_Valid_Renter_Review()
    {
        var (ownerId, renterId, _, bookingId) = await SeedCompletedBookingAsync();

        var token  = TestJwtTokenHelper.GenerateToken(renterId, $"{renterId:N}@rev-renter.local");
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.PostAsJsonAsync("/api/reviews", new
        {
            bookingId,
            rating  = 5,
            comment = "Great experience!"
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("Renter",          doc.RootElement.GetProperty("reviewerRole").GetString());
        Assert.Equal(5,                 doc.RootElement.GetProperty("rating").GetInt32());
        Assert.Equal("Great experience!", doc.RootElement.GetProperty("comment").GetString());
        Assert.Equal(ownerId.ToString(), doc.RootElement.GetProperty("revieweeId").GetString());
    }

    [Fact]
    public async Task Create_Returns_201_For_Valid_Owner_Review()
    {
        var (ownerId, renterId, _, bookingId) = await SeedCompletedBookingAsync();

        var token  = TestJwtTokenHelper.GenerateToken(ownerId, $"{ownerId:N}@rev-owner.local");
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.PostAsJsonAsync("/api/reviews", new
        {
            bookingId,
            rating = 4
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("Owner",             doc.RootElement.GetProperty("reviewerRole").GetString());
        Assert.Equal(renterId.ToString(), doc.RootElement.GetProperty("revieweeId").GetString());
    }

    [Fact]
    public async Task Create_Returns_401_When_Not_Authenticated()
    {
        var (_, _, _, bookingId) = await SeedCompletedBookingAsync();

        var response = await _factory.CreateClient().PostAsJsonAsync("/api/reviews", new
        {
            bookingId,
            rating = 5
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Create_Returns_409_For_Non_Completed_Booking()
    {
        var ownerId    = Guid.NewGuid();
        var renterId   = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var listingId  = Guid.NewGuid();
        var bookingId  = Guid.NewGuid();

        await _factory.SeedAsync(
            TestData.User(ownerId,  $"{ownerId:N}@rev2-owner.local"),
            TestData.User(renterId, $"{renterId:N}@rev2-renter.local"),
            TestData.Category(categoryId));

        await _factory.SeedAsync(
            TestData.Listing(listingId, ownerId, categoryId, ListingStatus.Approved));

        await _factory.SeedAsync(
            TestData.Booking(bookingId, listingId, renterId,
                DateOnly.FromDateTime(DateTime.UtcNow.AddDays(5)),
                DateOnly.FromDateTime(DateTime.UtcNow.AddDays(8)),
                BookingStatus.Approved));

        var token  = TestJwtTokenHelper.GenerateToken(renterId, $"{renterId:N}@rev2-renter.local");
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.PostAsJsonAsync("/api/reviews", new { bookingId, rating = 5 });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("review.booking_not_completed", doc.RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public async Task Create_Returns_409_For_Duplicate_Review()
    {
        var (_, renterId, _, bookingId) = await SeedCompletedBookingAsync();

        var token  = TestJwtTokenHelper.GenerateToken(renterId, $"{renterId:N}@rev-renter.local");
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        await client.PostAsJsonAsync("/api/reviews", new { bookingId, rating = 5 });
        var response = await client.PostAsJsonAsync("/api/reviews", new { bookingId, rating = 3 });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("review.already_submitted", doc.RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public async Task Create_Returns_403_For_Unrelated_User()
    {
        var (_, _, _, bookingId) = await SeedCompletedBookingAsync();

        var strangerId = Guid.NewGuid();
        await _factory.SeedAsync(TestData.User(strangerId, $"{strangerId:N}@stranger.local"));

        var token  = TestJwtTokenHelper.GenerateToken(strangerId, $"{strangerId:N}@stranger.local");
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.PostAsJsonAsync("/api/reviews", new { bookingId, rating = 5 });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Create_Returns_400_For_Invalid_Rating()
    {
        var (_, renterId, _, bookingId) = await SeedCompletedBookingAsync();

        var token  = TestJwtTokenHelper.GenerateToken(renterId, $"{renterId:N}@rev-renter.local");
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.PostAsJsonAsync("/api/reviews", new { bookingId, rating = 6 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("review.invalid_rating", doc.RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public async Task GetByListing_Returns_200_With_Reviews()
    {
        var (_, renterId, listingId, bookingId) = await SeedCompletedBookingAsync();

        var token  = TestJwtTokenHelper.GenerateToken(renterId, $"{renterId:N}@rev-renter.local");
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        await client.PostAsJsonAsync("/api/reviews", new { bookingId, rating = 5, comment = "Excellent!" });

        var response = await _factory.CreateClient().GetAsync($"/api/reviews/listing/{listingId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var reviews = doc.RootElement.EnumerateArray().ToList();
        Assert.NotEmpty(reviews);
        Assert.Equal("Renter", reviews[0].GetProperty("reviewerRole").GetString());
    }

    [Fact]
    public async Task GetByUser_Returns_200_With_Reviews()
    {
        var (ownerId, renterId, _, bookingId) = await SeedCompletedBookingAsync();

        var token  = TestJwtTokenHelper.GenerateToken(renterId, $"{renterId:N}@rev-renter.local");
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        await client.PostAsJsonAsync("/api/reviews", new { bookingId, rating = 4 });

        // Owner is the reviewee — query reviews received by the owner.
        var response = await _factory.CreateClient().GetAsync($"/api/reviews/user/{ownerId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var reviews = doc.RootElement.EnumerateArray().ToList();
        Assert.NotEmpty(reviews);
        Assert.Equal(ownerId.ToString(), reviews[0].GetProperty("revieweeId").GetString());
    }

    // -----------------------------------------------------------------------
    // Summary endpoints
    // -----------------------------------------------------------------------

    [Fact]
    public async Task GetListingSummary_Returns_200_With_Correct_Values()
    {
        var (_, renterId, listingId, bookingId) = await SeedCompletedBookingAsync();

        var token  = TestJwtTokenHelper.GenerateToken(renterId, $"{renterId:N}@rev-renter.local");
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        await client.PostAsJsonAsync("/api/reviews", new { bookingId, rating = 5 });

        var response = await _factory.CreateClient().GetAsync($"/api/reviews/listing/{listingId}/summary");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(1,   doc.RootElement.GetProperty("reviewCount").GetInt32());
        Assert.Equal(5.0, doc.RootElement.GetProperty("averageRating").GetDouble());
    }

    [Fact]
    public async Task GetListingSummary_Returns_Zero_When_No_Reviews()
    {
        var (_, _, listingId, _) = await SeedCompletedBookingAsync();

        var response = await _factory.CreateClient().GetAsync($"/api/reviews/listing/{listingId}/summary");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(0,   doc.RootElement.GetProperty("reviewCount").GetInt32());
        Assert.Equal(0.0, doc.RootElement.GetProperty("averageRating").GetDouble());
    }

    [Fact]
    public async Task GetListingSummary_Excludes_Owner_Reviews()
    {
        var (ownerId, renterId, listingId, bookingId) = await SeedCompletedBookingAsync();

        // Owner submits a review of the renter — must NOT affect the listing summary.
        var ownerToken = TestJwtTokenHelper.GenerateToken(ownerId, $"{ownerId:N}@rev-owner.local");
        var ownerClient = _factory.CreateClient();
        ownerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);
        await ownerClient.PostAsJsonAsync("/api/reviews", new { bookingId, rating = 1 });

        var response = await _factory.CreateClient().GetAsync($"/api/reviews/listing/{listingId}/summary");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(0,   doc.RootElement.GetProperty("reviewCount").GetInt32());
        Assert.Equal(0.0, doc.RootElement.GetProperty("averageRating").GetDouble());
    }

    [Fact]
    public async Task GetUserSummary_Returns_200_With_Correct_Values()
    {
        var (ownerId, renterId, _, bookingId) = await SeedCompletedBookingAsync();

        var token  = TestJwtTokenHelper.GenerateToken(renterId, $"{renterId:N}@rev-renter.local");
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        await client.PostAsJsonAsync("/api/reviews", new { bookingId, rating = 4 });

        var response = await _factory.CreateClient().GetAsync($"/api/reviews/user/{ownerId}/summary");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(1,   doc.RootElement.GetProperty("reviewCount").GetInt32());
        Assert.Equal(4.0, doc.RootElement.GetProperty("averageRating").GetDouble());
    }

    [Fact]
    public async Task GetUserSummary_Returns_Zero_When_No_Reviews()
    {
        var (ownerId, _, _, _) = await SeedCompletedBookingAsync();

        var response = await _factory.CreateClient().GetAsync($"/api/reviews/user/{ownerId}/summary");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(0,   doc.RootElement.GetProperty("reviewCount").GetInt32());
        Assert.Equal(0.0, doc.RootElement.GetProperty("averageRating").GetDouble());
    }
}
