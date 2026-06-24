using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Api;

// Exercises the booking HTTP layer end-to-end: routing, authentication, serialization,
// status-code mapping, and conflict detection — all against in-memory SQLite.
[Collection("Integration")]
public sealed class BookingHttpTests
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    private readonly RentalPlatformWebAppFactory _factory;

    public BookingHttpTests(RentalPlatformWebAppFactory factory) => _factory = factory;

    // Seeds the minimal graph needed by a booking test: owner + renter + category + listing.
    // Each call uses unique GUIDs so tests in the shared DB never conflict.
    private async Task<(Guid OwnerId, Guid RenterId, Guid ListingId)> SeedBaselineAsync()
    {
        var ownerId = Guid.NewGuid();
        var renterId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var listingId = Guid.NewGuid();

        await _factory.SeedAsync(
            TestData.User(ownerId, $"{ownerId:N}@booking-owner.local"),
            TestData.User(renterId, $"{renterId:N}@booking-renter.local"),
            TestData.Category(categoryId));

        await _factory.SeedAsync(
            TestData.Listing(listingId, ownerId, categoryId, ListingStatus.Approved));

        return (ownerId, renterId, listingId);
    }

    [Fact]
    public async Task Create_Returns_200_With_Pending_Booking()
    {
        var (_, renterId, listingId) = await SeedBaselineAsync();

        var token = TestJwtTokenHelper.GenerateToken(renterId, $"{renterId:N}@booking-renter.local");
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.PostAsJsonAsync("/api/bookings", new
        {
            listingId,
            startDate = Today.AddDays(30).ToString("yyyy-MM-dd"),
            endDate = Today.AddDays(35).ToString("yyyy-MM-dd")
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("Pending", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal(listingId.ToString(), doc.RootElement.GetProperty("listingId").GetString());
    }

    [Fact]
    public async Task Create_Returns_409_When_Dates_Overlap_Pending_Booking()
    {
        var (_, renterId, listingId) = await SeedBaselineAsync();
        var blockerId = Guid.NewGuid();

        // Pre-seed a blocking pending booking from another renter.
        var otherRenterId = Guid.NewGuid();
        await _factory.SeedAsync(TestData.User(otherRenterId, $"{otherRenterId:N}@other.local"));
        await _factory.SeedAsync(TestData.Booking(
            blockerId, listingId, otherRenterId,
            Today.AddDays(40), Today.AddDays(45),
            BookingStatus.Pending,
            expiresAt: DateTime.UtcNow.AddHours(24)));

        var token = TestJwtTokenHelper.GenerateToken(renterId, $"{renterId:N}@booking-renter.local");
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.PostAsJsonAsync("/api/bookings", new
        {
            listingId,
            startDate = Today.AddDays(41).ToString("yyyy-MM-dd"),
            endDate = Today.AddDays(43).ToString("yyyy-MM-dd")
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        // Verify RFC 7807 structure.
        Assert.Equal("booking.overlap", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal(409, doc.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("application/problem+json",
            response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Cancel_Returns_200_For_Pending_Booking_Before_Start_Date()
    {
        var (_, renterId, listingId) = await SeedBaselineAsync();
        var bookingId = Guid.NewGuid();

        await _factory.SeedAsync(TestData.Booking(
            bookingId, listingId, renterId,
            Today.AddDays(50), Today.AddDays(55),
            BookingStatus.Pending,
            expiresAt: DateTime.UtcNow.AddHours(24)));

        var token = TestJwtTokenHelper.GenerateToken(renterId, $"{renterId:N}@booking-renter.local");
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.PostAsync($"/api/bookings/{bookingId}/cancel", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("Cancelled", doc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Cancel_Returns_409_For_Approved_Booking_On_Start_Date()
    {
        var (_, renterId, listingId) = await SeedBaselineAsync();
        var bookingId = Guid.NewGuid();

        // Start date is today — rental window has begun, cancel is blocked.
        await _factory.SeedAsync(TestData.Booking(
            bookingId, listingId, renterId,
            Today, Today.AddDays(3),
            BookingStatus.Approved));

        var token = TestJwtTokenHelper.GenerateToken(renterId, $"{renterId:N}@booking-renter.local");
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.PostAsync($"/api/bookings/{bookingId}/cancel", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("booking.not_cancellable", doc.RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public async Task Create_Returns_401_For_Unauthenticated_Request()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/bookings", new
        {
            listingId = Guid.NewGuid(),
            startDate = Today.AddDays(60).ToString("yyyy-MM-dd"),
            endDate = Today.AddDays(65).ToString("yyyy-MM-dd")
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Owner_Activates_Then_Completes_Booking()
    {
        var (ownerId, renterId, listingId) = await SeedBaselineAsync();
        var bookingId = Guid.NewGuid();

        await _factory.SeedAsync(TestData.Booking(
            bookingId, listingId, renterId,
            Today.AddDays(-5), Today.AddDays(-1),
            BookingStatus.Approved));

        var ownerToken = TestJwtTokenHelper.GenerateToken(ownerId, $"{ownerId:N}@booking-owner.local");
        var ownerClient = _factory.CreateClient();
        ownerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);

        var activateResponse = await ownerClient.PostAsync($"/api/bookings/{bookingId}/activate", null);
        Assert.Equal(HttpStatusCode.OK, activateResponse.StatusCode);
        using (var activateDoc = JsonDocument.Parse(await activateResponse.Content.ReadAsStringAsync()))
        {
            Assert.Equal("Active", activateDoc.RootElement.GetProperty("status").GetString());
        }

        var completeResponse = await ownerClient.PostAsync($"/api/bookings/{bookingId}/complete", null);
        Assert.Equal(HttpStatusCode.OK, completeResponse.StatusCode);
        using (var completeDoc = JsonDocument.Parse(await completeResponse.Content.ReadAsStringAsync()))
        {
            Assert.Equal("Completed", completeDoc.RootElement.GetProperty("status").GetString());
        }
    }

    [Fact]
    public async Task Activate_By_Renter_Returns_403()
    {
        var (_, renterId, listingId) = await SeedBaselineAsync();
        var bookingId = Guid.NewGuid();

        await _factory.SeedAsync(TestData.Booking(
            bookingId, listingId, renterId,
            Today.AddDays(-5), Today.AddDays(-1),
            BookingStatus.Approved));

        var token = TestJwtTokenHelper.GenerateToken(renterId, $"{renterId:N}@booking-renter.local");
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.PostAsync($"/api/bookings/{bookingId}/activate", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetById_Returns_Detail_For_Renter()
    {
        var (_, renterId, listingId) = await SeedBaselineAsync();
        var bookingId = Guid.NewGuid();

        await _factory.SeedAsync(TestData.Booking(
            bookingId, listingId, renterId,
            Today.AddDays(-5), Today.AddDays(-1),
            BookingStatus.Approved));

        var token = TestJwtTokenHelper.GenerateToken(renterId, $"{renterId:N}@booking-renter.local");
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync($"/api/bookings/{bookingId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("renter", doc.RootElement.GetProperty("role").GetString());
    }
}
