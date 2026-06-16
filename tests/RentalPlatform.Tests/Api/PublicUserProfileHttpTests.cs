using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Api;

[Collection("Integration")]
public sealed class PublicUserProfileHttpTests
{
    private static readonly DateOnly PastStart = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-10));
    private static readonly DateOnly PastEnd   = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-3));

    private readonly RentalPlatformWebAppFactory _factory;

    public PublicUserProfileHttpTests(RentalPlatformWebAppFactory factory) => _factory = factory;

    // Seeds owner with one approved listing and one completed booking from a renter.
    private async Task<(Guid OwnerId, Guid RenterId, Guid ListingId, Guid BookingId)> SeedOwnerAsync()
    {
        var ownerId    = Guid.NewGuid();
        var renterId   = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var listingId  = Guid.NewGuid();
        var bookingId  = Guid.NewGuid();

        await _factory.SeedAsync(
            TestData.User(ownerId,  $"{ownerId:N}@profile-owner.local"),
            TestData.User(renterId, $"{renterId:N}@profile-renter.local"),
            TestData.Category(categoryId));

        await _factory.SeedAsync(
            TestData.Listing(listingId, ownerId, categoryId, ListingStatus.Approved));

        await _factory.SeedAsync(
            TestData.Booking(bookingId, listingId, renterId, PastStart, PastEnd, BookingStatus.Completed));

        return (ownerId, renterId, listingId, bookingId);
    }

    // -----------------------------------------------------------------------
    // Basic profile shape
    // -----------------------------------------------------------------------

    [Fact]
    public async Task GetPublicProfile_Returns_200_For_Existing_User()
    {
        var (ownerId, _, _, _) = await SeedOwnerAsync();

        var response = await _factory.CreateClient().GetAsync($"/api/users/{ownerId}/public-profile");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetPublicProfile_Returns_404_For_Missing_User()
    {
        var response = await _factory.CreateClient().GetAsync($"/api/users/{Guid.NewGuid()}/public-profile");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetPublicProfile_Returns_Expected_Public_Fields()
    {
        var (ownerId, _, _, _) = await SeedOwnerAsync();

        var response = await _factory.CreateClient().GetAsync($"/api/users/{ownerId}/public-profile");
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        Assert.Equal(ownerId.ToString(), root.GetProperty("id").GetString());
        Assert.False(string.IsNullOrEmpty(root.GetProperty("firstName").GetString()));
        Assert.False(string.IsNullOrEmpty(root.GetProperty("lastName").GetString()));
        Assert.True(root.TryGetProperty("avatarUrl", out _));
        Assert.True(root.TryGetProperty("memberSince", out _));
        Assert.True(root.TryGetProperty("averageRating", out _));
        Assert.True(root.TryGetProperty("reviewCount", out _));
        Assert.True(root.TryGetProperty("activeListingsCount", out _));
    }

    // -----------------------------------------------------------------------
    // Privacy: sensitive fields must NOT be present
    // -----------------------------------------------------------------------

    [Fact]
    public async Task GetPublicProfile_Does_Not_Expose_Email_Or_Phone()
    {
        var (ownerId, _, _, _) = await SeedOwnerAsync();

        var response = await _factory.CreateClient().GetAsync($"/api/users/{ownerId}/public-profile");
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        Assert.False(root.TryGetProperty("email", out _),         "email must not be exposed");
        Assert.False(root.TryGetProperty("phoneNumber", out _),   "phoneNumber must not be exposed");
        Assert.False(root.TryGetProperty("passwordHash", out _),  "passwordHash must not be exposed");
        Assert.False(root.TryGetProperty("role", out _),          "role must not be exposed");
        Assert.False(root.TryGetProperty("isBlocked", out _),     "isBlocked must not be exposed");
        Assert.False(root.TryGetProperty("externalAuthProvider", out _), "externalAuthProvider must not be exposed");
    }

    // -----------------------------------------------------------------------
    // Active listings count
    // -----------------------------------------------------------------------

    [Fact]
    public async Task GetPublicProfile_Returns_Correct_ActiveListingsCount()
    {
        var (ownerId, _, _, _) = await SeedOwnerAsync();

        var response = await _factory.CreateClient().GetAsync($"/api/users/{ownerId}/public-profile");
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);

        // SeedOwnerAsync seeded exactly one Approved listing.
        Assert.Equal(1, doc.RootElement.GetProperty("activeListingsCount").GetInt32());
    }

    [Fact]
    public async Task GetPublicProfile_Does_Not_Count_Non_Approved_Listings()
    {
        var ownerId    = Guid.NewGuid();
        var categoryId = Guid.NewGuid();

        await _factory.SeedAsync(
            TestData.User(ownerId, $"{ownerId:N}@profile2-owner.local"),
            TestData.Category(categoryId));

        // Only PendingApproval listings — none should count.
        await _factory.SeedAsync(
            TestData.Listing(Guid.NewGuid(), ownerId, categoryId, ListingStatus.PendingApproval),
            TestData.Listing(Guid.NewGuid(), ownerId, categoryId, ListingStatus.Rejected));

        var response = await _factory.CreateClient().GetAsync($"/api/users/{ownerId}/public-profile");
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);

        Assert.Equal(0, doc.RootElement.GetProperty("activeListingsCount").GetInt32());
    }

    // -----------------------------------------------------------------------
    // Rating summary
    // -----------------------------------------------------------------------

    [Fact]
    public async Task GetPublicProfile_Returns_Zero_Rating_When_No_Reviews()
    {
        var (ownerId, _, _, _) = await SeedOwnerAsync();

        var response = await _factory.CreateClient().GetAsync($"/api/users/{ownerId}/public-profile");
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);

        Assert.Equal(0,   doc.RootElement.GetProperty("reviewCount").GetInt32());
        Assert.Equal(0.0, doc.RootElement.GetProperty("averageRating").GetDouble());
    }

    [Fact]
    public async Task GetPublicProfile_Hides_Rating_Below_Two_Owner_Reviews()
    {
        var (ownerId, renterId, _, bookingId) = await SeedOwnerAsync();

        await SubmitOwnerReviewAsync(renterId, $"{renterId:N}@profile-renter.local", bookingId, 4);

        var response = await _factory.CreateClient().GetAsync($"/api/users/{ownerId}/public-profile");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        // One review: count is shown, but the aggregate average is hidden (min 2).
        Assert.Equal(1,   doc.RootElement.GetProperty("reviewCount").GetInt32());
        Assert.Equal(0.0, doc.RootElement.GetProperty("averageRating").GetDouble());
    }

    [Fact]
    public async Task GetPublicProfile_Returns_Correct_Rating_With_Two_Owner_Reviews()
    {
        var (ownerId, renterId, listingId, bookingId) = await SeedOwnerAsync();

        // A second renter + completed booking, so the owner accrues two reviews.
        var renter2 = Guid.NewGuid();
        var booking2 = Guid.NewGuid();
        await _factory.SeedAsync(TestData.User(renter2, $"{renter2:N}@profile-renter2.local"));
        await _factory.SeedAsync(TestData.Booking(booking2, listingId, renter2, PastStart, PastEnd, BookingStatus.Completed));

        await SubmitOwnerReviewAsync(renterId, $"{renterId:N}@profile-renter.local", bookingId, 4);
        await SubmitOwnerReviewAsync(renter2, $"{renter2:N}@profile-renter2.local", booking2, 4);

        var response = await _factory.CreateClient().GetAsync($"/api/users/{ownerId}/public-profile");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(2,   doc.RootElement.GetProperty("reviewCount").GetInt32());
        Assert.Equal(4.0, doc.RootElement.GetProperty("averageRating").GetDouble());
    }

    private async Task SubmitOwnerReviewAsync(Guid renterId, string renterEmail, Guid bookingId, int score)
    {
        var token  = TestJwtTokenHelper.GenerateToken(renterId, renterEmail);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        await client.PostAsJsonAsync("/api/reviews/owner", new
        {
            bookingId,
            communicationRating = score,
            pickupHandoverRating = score,
            friendlinessRating = score
        });
    }

    // -----------------------------------------------------------------------
    // Anonymous access
    // -----------------------------------------------------------------------

    [Fact]
    public async Task GetPublicProfile_Is_Accessible_Without_Auth()
    {
        var (ownerId, _, _, _) = await SeedOwnerAsync();

        // No Authorization header — must still return 200.
        var response = await _factory.CreateClient().GetAsync($"/api/users/{ownerId}/public-profile");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
