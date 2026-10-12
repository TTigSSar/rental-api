using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Api;

// ADR-030 §11 (decision F3): the exact list of write paths that must refuse a blocked caller who
// still holds a valid JWT. There is deliberately NO global middleware / OnTokenValidated check (it
// would break the moderation appeal and cost a DB hit per request), so every write path enforces
// IsBlocked itself — and this matrix is what notices when a new or edited path forgets to.
//
// One request per write group, each asserting 403 AND the group's errorCode. Deliberately NOT in
// the list: chat mark-read and notification mark-read (harmless, left open), and sending a message
// in the blocked member's own Moderation thread (the appeal carve-out, pinned below as NOT 403).
[Collection("Integration")]
public sealed class BlockedUserWriteMatrixHttpTests
{
    private readonly RentalPlatformWebAppFactory _factory;

    public BlockedUserWriteMatrixHttpTests(RentalPlatformWebAppFactory factory) => _factory = factory;

    private sealed record Fixture(
        Guid BlockedId,
        string BlockedEmail,
        Guid OtherOwnerId,
        Guid CategoryId,
        Guid OwnListingId,
        Guid OwnImageId,
        Guid OthersListingId,
        Guid PendingBookingId,
        Guid CompletedBookingId,
        Guid BookingConversationId);

    private const string Password = "Blocked1234";

    private async Task<Fixture> SeedAsync()
    {
        var blockedId = Guid.NewGuid();
        var blockedEmail = $"{blockedId:N}@blocked-matrix.local";
        var otherId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var ownListingId = Guid.NewGuid();
        var ownImageId = Guid.NewGuid();
        var othersListingId = Guid.NewGuid();
        var pendingBookingId = Guid.NewGuid();
        var completedBookingId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();

        await _factory.SeedAsync(
            TestData.User(blockedId, blockedEmail, isBlocked: true, passwordHash: BCrypt.Net.BCrypt.HashPassword(Password)),
            TestData.OwnerWithHome(otherId, $"{otherId:N}@matrix-owner.local", TestData.KentronPoint),
            TestData.Category(categoryId));

        await _factory.SeedAsync(
            TestData.Listing(ownListingId, blockedId, categoryId, ListingStatus.Approved),
            TestData.Listing(othersListingId, otherId, categoryId, ListingStatus.Approved));
        await _factory.SeedAsync(TestData.Image(ownImageId, ownListingId, isPrimary: true, sortOrder: 0));

        var start = TestData.Today.AddDays(10);
        await _factory.SeedAsync(
            TestData.Booking(pendingBookingId, othersListingId, blockedId, start, start.AddDays(2), BookingStatus.Pending),
            TestData.Booking(completedBookingId, othersListingId, blockedId, TestData.Today.AddDays(-10), TestData.Today.AddDays(-8), BookingStatus.Completed));
        await _factory.SeedAsync(TestData.Conversation(conversationId, pendingBookingId, otherId, blockedId));

        return new Fixture(blockedId, blockedEmail, otherId, categoryId, ownListingId, ownImageId,
            othersListingId, pendingBookingId, completedBookingId, conversationId);
    }

    private HttpClient BlockedClient(Fixture f, string? simulatedIp = null)
    {
        var client = _factory.CreateClient();
        // Upload endpoints sit behind a per-IP rate-limit bucket shared with other test classes
        // (ImageHttpTests); a distinct simulated IP keeps this class out of it.
        if (simulatedIp is not null)
        {
            client.DefaultRequestHeaders.Add(TestRemoteIpStartupFilter.HeaderName, simulatedIp);
        }

        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestJwtTokenHelper.GenerateToken(f.BlockedId, f.BlockedEmail));
        return client;
    }

    private static async Task AssertForbiddenAsync(HttpResponseMessage response, string errorCode)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"Expected 403, got {(int)response.StatusCode}: {body}");
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(errorCode, doc.RootElement.GetProperty("errorCode").GetString());
    }

    // ---- Listings -------------------------------------------------------------------------------

    [Fact]
    public async Task Listing_Create_Returns_403_Listing_User_Blocked()
    {
        var f = await SeedAsync();
        var response = await BlockedClient(f).PostAsJsonAsync("/api/listings", new
        {
            categoryId = f.CategoryId,
            title = "Wooden Train Set",
            description = "A long enough description to satisfy validation rules.",
            pricePerDay = 2500,
            compensationAmount = 12000
        });
        await AssertForbiddenAsync(response, "listing.user_blocked");
    }

    [Fact]
    public async Task Listing_Update_Returns_403_Listing_User_Blocked()
    {
        var f = await SeedAsync();
        var response = await BlockedClient(f).PatchAsJsonAsync($"/api/listings/{f.OwnListingId}", new { pricePerDay = 3100 });
        await AssertForbiddenAsync(response, "listing.user_blocked");
    }

    // The block check runs before the listing lookup, so a blocked caller learns nothing about a
    // listing they do not own either: same 403 whether or not the id exists.
    [Fact]
    public async Task Listing_Update_On_Unknown_Listing_Is_Still_403_Not_404()
    {
        var f = await SeedAsync();
        var response = await BlockedClient(f).PatchAsJsonAsync($"/api/listings/{Guid.NewGuid()}", new { pricePerDay = 3100 });
        await AssertForbiddenAsync(response, "listing.user_blocked");
    }

    [Theory]
    [InlineData("archive")]
    [InlineData("restore")]
    [InlineData("resubmit")]
    public async Task Listing_State_Action_Returns_403_Listing_User_Blocked(string action)
    {
        var f = await SeedAsync();
        var response = await BlockedClient(f).PostAsync($"/api/listings/{f.OwnListingId}/{action}", content: null);
        await AssertForbiddenAsync(response, "listing.user_blocked");
    }

    [Fact]
    public async Task Listing_Image_Upload_Returns_403_Listing_User_Blocked()
    {
        var f = await SeedAsync();
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(TestData.PngBytes());
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "files", "toy.png");

        var response = await BlockedClient(f, "10.99.88.4").PostAsync($"/api/listings/{f.OwnListingId}/images", form);
        await AssertForbiddenAsync(response, "listing.user_blocked");
    }

    [Fact]
    public async Task Listing_Image_Delete_Returns_403_Listing_User_Blocked()
    {
        var f = await SeedAsync();
        var response = await BlockedClient(f).DeleteAsync($"/api/listings/{f.OwnListingId}/images/{f.OwnImageId}");
        await AssertForbiddenAsync(response, "listing.user_blocked");
    }

    [Fact]
    public async Task Listing_Image_Reorder_Returns_403_Listing_User_Blocked()
    {
        var f = await SeedAsync();
        var response = await BlockedClient(f).PutAsJsonAsync(
            $"/api/listings/{f.OwnListingId}/images/order",
            new { imageIds = new[] { f.OwnImageId } });
        await AssertForbiddenAsync(response, "listing.user_blocked");
    }

    // A valid JWT whose user row no longer exists is "not authenticated" (401), not "blocked" (403):
    // pins ResolveActiveOwnerAsync's missing-user branch, same answer as CreateAsync/GetMineAsync.
    [Fact]
    public async Task Listing_Update_With_Valid_Jwt_But_No_User_Row_Returns_401()
    {
        var f = await SeedAsync();
        var ghostId = Guid.NewGuid();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestJwtTokenHelper.GenerateToken(ghostId, $"{ghostId:N}@ghost.local"));

        var response = await client.PatchAsJsonAsync($"/api/listings/{f.OwnListingId}", new { pricePerDay = 3100 });

        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, $"Expected 401, got {(int)response.StatusCode}: {body}");
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("listing.unauthenticated", doc.RootElement.GetProperty("errorCode").GetString());
    }

    // ---- Bookings -------------------------------------------------------------------------------

    [Fact]
    public async Task Booking_Create_Returns_403_Booking_User_Blocked()
    {
        var f = await SeedAsync();
        var start = TestData.Today.AddDays(30);
        var response = await BlockedClient(f).PostAsJsonAsync("/api/bookings", new
        {
            listingId = f.OthersListingId,
            startDate = start.ToString("yyyy-MM-dd"),
            endDate = start.AddDays(2).ToString("yyyy-MM-dd")
        });
        await AssertForbiddenAsync(response, "booking.user_blocked");
    }

    [Fact]
    public async Task Booking_Cancel_Returns_403_Booking_User_Blocked()
    {
        var f = await SeedAsync();
        var response = await BlockedClient(f).PostAsync($"/api/bookings/{f.PendingBookingId}/cancel", content: null);
        await AssertForbiddenAsync(response, "booking.user_blocked");
    }

    // Owner-side decisions share one guard; approve is the representative. The blocked caller here is
    // the renter, so the 403 also shows the block check runs before any ownership check.
    [Theory]
    [InlineData("approve")]
    [InlineData("reject")]
    public async Task Booking_Owner_Decision_Returns_403_Booking_User_Blocked(string action)
    {
        var f = await SeedAsync();
        var response = await BlockedClient(f).PostAsync($"/api/bookings/{f.PendingBookingId}/{action}", content: null);
        await AssertForbiddenAsync(response, "booking.user_blocked");
    }

    // ---- Chat -----------------------------------------------------------------------------------

    [Fact]
    public async Task Chat_GetOrCreate_For_Booking_Returns_403_Chat_User_Blocked()
    {
        var f = await SeedAsync();
        var response = await BlockedClient(f).PostAsync($"/api/chat/conversations/from-booking/{f.PendingBookingId}", content: null);
        await AssertForbiddenAsync(response, "chat.user_blocked");
    }

    [Fact]
    public async Task Chat_Send_Message_In_Booking_Conversation_Returns_403_Chat_User_Blocked()
    {
        var f = await SeedAsync();
        var response = await BlockedClient(f).PostAsJsonAsync("/api/chat/messages", new
        {
            conversationId = f.BookingConversationId,
            content = "hello"
        });
        await AssertForbiddenAsync(response, "chat.user_blocked");
    }

    // The deliberate appeal carve-out: a suspended member must still be able to write in their own
    // Moderation thread. If this ever turns 403 the appeal channel is broken.
    [Fact]
    public async Task Chat_Send_Message_In_Own_Moderation_Thread_Is_Allowed_Appeal_Carve_Out()
    {
        var f = await SeedAsync();
        var adminId = Guid.NewGuid();
        await _factory.SeedAsync(TestData.User(adminId, $"{adminId:N}@matrix-admin.local", role: UserRole.Admin));

        Guid moderationConversationId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IConversationsStore>();
            moderationConversationId = (await store.GetOrCreateForModerationAsync(adminId, f.BlockedId)).Id;
        }

        var response = await BlockedClient(f).PostAsJsonAsync("/api/chat/messages", new
        {
            conversationId = moderationConversationId,
            content = "Please review my suspension."
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static MultipartFormDataContent PngForm()
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(TestData.PngBytes());
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "image", "photo.png");
        return form;
    }

    [Fact]
    public async Task Chat_Send_Image_In_Booking_Conversation_Returns_403_Chat_User_Blocked()
    {
        var f = await SeedAsync();
        using var form = PngForm();
        var response = await BlockedClient(f, "10.99.88.2").PostAsync($"/api/chat/conversations/{f.BookingConversationId}/messages/image", form);
        await AssertForbiddenAsync(response, "chat.user_blocked");
    }

    // Same appeal carve-out as the text path (ChatService.SendImageMessageAsync).
    [Fact]
    public async Task Chat_Send_Image_In_Own_Moderation_Thread_Is_Not_403_Appeal_Carve_Out()
    {
        var f = await SeedAsync();
        var adminId = Guid.NewGuid();
        await _factory.SeedAsync(TestData.User(adminId, $"{adminId:N}@matrix-admin.local", role: UserRole.Admin));

        Guid moderationConversationId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IConversationsStore>();
            moderationConversationId = (await store.GetOrCreateForModerationAsync(adminId, f.BlockedId)).Id;
        }

        using var form = PngForm();
        var response = await BlockedClient(f, "10.99.88.3").PostAsync($"/api/chat/conversations/{moderationConversationId}/messages/image", form);

        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---- Favorites, reports, reviews ------------------------------------------------------------

    [Fact]
    public async Task Favorite_Add_Returns_403_Favorite_User_Blocked()
    {
        var f = await SeedAsync();
        var response = await BlockedClient(f).PostAsync($"/api/favorites/{f.OthersListingId}", content: null);
        await AssertForbiddenAsync(response, "favorite.user_blocked");
    }

    [Fact]
    public async Task Favorite_Remove_Returns_403_Favorite_User_Blocked()
    {
        var f = await SeedAsync();
        var response = await BlockedClient(f).DeleteAsync($"/api/favorites/{f.OthersListingId}");
        await AssertForbiddenAsync(response, "favorite.user_blocked");
    }

    [Fact]
    public async Task Report_Create_Returns_403_Report_User_Blocked()
    {
        var f = await SeedAsync();
        var response = await BlockedClient(f).PostAsJsonAsync("/api/reports", new
        {
            targetType = "listing",
            targetId = f.OthersListingId,
            reasonCode = "other"
        });
        await AssertForbiddenAsync(response, "report.user_blocked");
    }

    // Covered in depth by ReviewsHttpTests; one representative here so the matrix reads complete.
    [Fact]
    public async Task Review_Submit_Returns_403_Review_User_Blocked()
    {
        var f = await SeedAsync();
        var response = await BlockedClient(f).PostAsJsonAsync("/api/reviews/toy", new
        {
            bookingId = f.CompletedBookingId,
            overallRating = 5,
            conditionRating = 5,
            cleanlinessRating = 5,
            valueForMoneyRating = 5,
            funPlayValueRating = 5,
            descriptionAccuracyRating = 5
        });
        await AssertForbiddenAsync(response, "review.user_blocked");
    }

    // ---- Account --------------------------------------------------------------------------------

    [Fact]
    public async Task HomePoint_Put_Returns_403_Auth_User_Blocked()
    {
        var f = await SeedAsync();
        var response = await BlockedClient(f).PutAsJsonAsync("/api/auth/me/home-point", new
        {
            latitude = TestData.KentronPoint.Latitude,
            longitude = TestData.KentronPoint.Longitude
        });
        await AssertForbiddenAsync(response, "auth.user_blocked");
    }

    [Fact]
    public async Task HomePoint_Delete_Returns_403_Auth_User_Blocked()
    {
        var f = await SeedAsync();
        var response = await BlockedClient(f).DeleteAsync("/api/auth/me/home-point");
        await AssertForbiddenAsync(response, "auth.user_blocked");
    }

    [Fact]
    public async Task PreferredLanguage_Put_Returns_403_Auth_User_Blocked()
    {
        var f = await SeedAsync();
        var response = await BlockedClient(f).PutAsJsonAsync("/api/auth/me/preferred-language", new { preferredLanguage = "hy" });
        await AssertForbiddenAsync(response, "auth.user_blocked");
    }

    [Fact]
    public async Task Name_Put_Returns_403_Auth_User_Blocked()
    {
        var f = await SeedAsync();
        var response = await BlockedClient(f).PutAsJsonAsync("/api/auth/me/name", new { firstName = "Anna", lastName = "Petrosyan" });
        await AssertForbiddenAsync(response, "auth.user_blocked");
    }

    [Fact]
    public async Task Phone_Put_Returns_403_Auth_User_Blocked()
    {
        var f = await SeedAsync();
        var response = await BlockedClient(f).PutAsJsonAsync("/api/auth/me/phone", new { phoneNumber = "+374 99 123456" });
        await AssertForbiddenAsync(response, "auth.user_blocked");
    }

    [Fact]
    public async Task Password_Put_Returns_403_Auth_User_Blocked()
    {
        var f = await SeedAsync();
        var response = await BlockedClient(f).PutAsJsonAsync("/api/auth/me/password", new
        {
            currentPassword = Password,
            newPassword = "NewPassword2"
        });
        await AssertForbiddenAsync(response, "auth.user_blocked");
    }

    [Fact]
    public async Task Login_With_Correct_Password_Returns_403_Auth_User_Blocked()
    {
        var f = await SeedAsync();
        var client = _factory.CreateClient();
        // Own simulated IP: login shares the per-IP "auth" rate-limit bucket with other classes.
        client.DefaultRequestHeaders.Add(TestRemoteIpStartupFilter.HeaderName, "10.99.88.1");

        var response = await client.PostAsJsonAsync("/api/auth/login", new { email = f.BlockedEmail, password = Password });
        await AssertForbiddenAsync(response, "auth.user_blocked");
    }
}
