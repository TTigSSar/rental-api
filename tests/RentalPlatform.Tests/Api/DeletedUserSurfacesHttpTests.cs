using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Infrastructure.Persistence;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Api;

// ADR-056 (Trello AD-3): every API surface that returns a user as a counterparty / author / participant, through
// the REAL pipeline. A deleted user is seeded WITH a name and an avatar and only DeletedAt set, so each test proves
// that the projection itself blanks the name and avatar and raises isDeleted, rather than relying on the erase
// having already run. Each projection test pairs the deleted user with a live one (isDeleted false, name intact).
[Collection("Integration")]
public sealed class DeletedUserSurfacesHttpTests
{
    private const string LiveAvatar = "/uploads/avatars/live.png";
    private const string GoneAvatar = "/uploads/avatars/gone.png";

    private readonly RentalPlatformWebAppFactory _factory;

    public DeletedUserSurfacesHttpTests(RentalPlatformWebAppFactory factory) => _factory = factory;

    private sealed record Account(Guid Id, string Email, UserRole Role)
    {
        public string Token => TestJwtTokenHelper.GenerateToken(Id, Email, Role);
    }

    private HttpClient ClientFor(Account? account = null)
    {
        var client = _factory.CreateClient();
        if (account is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.Token);
        }

        return client;
    }

    private async Task<Account> LiveAsync(string tag, UserRole role = UserRole.User, bool home = true, string? firstName = null)
    {
        var id = Guid.NewGuid();
        var email = $"{id:N}@{tag}.local";
        var user = home
            ? TestData.OwnerWithHome(id, email)
            : TestData.User(id, email, role: role, isIdConfirmed: role == UserRole.Admin);
        user.Role = role;
        user.FirstName = firstName ?? "Live";
        user.LastName = "Person";
        user.AvatarUrl = LiveAvatar;
        await _factory.SeedAsync(user);
        return new Account(id, email, role);
    }

    // DeletedAt is set but the name, the avatar and the email are deliberately left in place.
    private async Task<Account> DeletedAsync(string tag, string? firstName = null)
    {
        var id = Guid.NewGuid();
        var email = $"{id:N}@{tag}.local";
        var user = TestData.User(id, email);
        user.FirstName = firstName ?? "Gone";
        user.LastName = "Person";
        user.AvatarUrl = GoneAvatar;
        user.DeletedAt = DateTime.UtcNow;
        await _factory.SeedAsync(user);
        return new Account(id, email, UserRole.User);
    }

    private async Task<Guid> ListingAsync(Guid ownerId, ListingStatus status = ListingStatus.Approved, string? title = null)
    {
        var categoryId = Guid.NewGuid();
        var listingId = Guid.NewGuid();
        var listing = TestData.Listing(listingId, ownerId, categoryId, status);
        if (title is not null)
        {
            listing.Title = title;
        }

        await _factory.SeedAsync(TestData.Category(categoryId), listing);
        return listingId;
    }

    private async Task<Guid> BookingAsync(Guid listingId, Guid renterId, BookingStatus status)
    {
        var bookingId = Guid.NewGuid();
        var past = status == BookingStatus.Completed;
        var start = past ? TestData.Today.AddDays(-10) : TestData.Today.AddDays(10);
        await _factory.SeedAsync(TestData.Booking(bookingId, listingId, renterId, start, start.AddDays(2), status));
        return bookingId;
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        Assert.Equal(expected, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static JsonElement ById(JsonElement array, string property, Guid id) =>
        array.EnumerateArray().Single(item => item.GetProperty(property).GetGuid() == id);

    // ---- Bookings ------------------------------------------------------------------------------

    [Fact]
    public async Task Booking_Detail_Shows_The_Deleted_Counterparty_As_Deleted_And_A_Live_One_As_Live()
    {
        var owner = await LiveAsync("bk-detail");
        var deletedRenter = await DeletedAsync("bk-detail");
        var liveRenter = await LiveAsync("bk-detail", home: false);
        var listingId = await ListingAsync(owner.Id);
        var withDeleted = await BookingAsync(listingId, deletedRenter.Id, BookingStatus.Completed);
        var withLive = await BookingAsync(listingId, liveRenter.Id, BookingStatus.Completed);
        var client = ClientFor(owner);

        var deleted = await JsonAsync(await client.GetAsync($"/api/bookings/{withDeleted}"));
        var live = await JsonAsync(await client.GetAsync($"/api/bookings/{withLive}"));

        Assert.True(deleted.GetProperty("counterpartyIsDeleted").GetBoolean());
        Assert.Equal(string.Empty, deleted.GetProperty("counterpartyFirstName").GetString());
        Assert.Equal(string.Empty, deleted.GetProperty("counterpartyLastName").GetString());
        Assert.Equal(JsonValueKind.Null, deleted.GetProperty("counterpartyAvatarUrl").ValueKind);
        Assert.False(live.GetProperty("counterpartyIsDeleted").GetBoolean());
        Assert.Equal("Live", live.GetProperty("counterpartyFirstName").GetString());
        Assert.Equal(LiveAvatar, live.GetProperty("counterpartyAvatarUrl").GetString());
    }

    [Fact]
    public async Task Booking_Detail_Renter_Side_Shows_A_Deleted_Owner_As_Deleted()
    {
        var deletedOwner = await DeletedAsync("bk-detail-owner");
        var renter = await LiveAsync("bk-detail-owner", home: false);
        var listingId = await ListingAsync(deletedOwner.Id, ListingStatus.Archived);
        var bookingId = await BookingAsync(listingId, renter.Id, BookingStatus.Completed);

        var body = await JsonAsync(await ClientFor(renter).GetAsync($"/api/bookings/{bookingId}"));

        Assert.True(body.GetProperty("counterpartyIsDeleted").GetBoolean());
        Assert.Equal(string.Empty, body.GetProperty("counterpartyFirstName").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("counterpartyAvatarUrl").ValueKind);
    }

    [Fact]
    public async Task Incoming_Requests_Show_A_Deleted_Renter_As_Deleted_With_No_Name_Or_Email()
    {
        var owner = await LiveAsync("bk-requests");
        var deletedRenter = await DeletedAsync("bk-requests");
        var liveRenter = await LiveAsync("bk-requests", home: false);
        var listingId = await ListingAsync(owner.Id);
        var fromDeleted = await BookingAsync(listingId, deletedRenter.Id, BookingStatus.Cancelled);
        var fromLive = await BookingAsync(listingId, liveRenter.Id, BookingStatus.Cancelled);

        var list = await JsonAsync(await ClientFor(owner).GetAsync("/api/bookings/requests"));

        var deleted = ById(list, "id", fromDeleted);
        var live = ById(list, "id", fromLive);
        Assert.True(deleted.GetProperty("renterIsDeleted").GetBoolean());
        Assert.Equal(string.Empty, deleted.GetProperty("renterFirstName").GetString());
        Assert.Equal(string.Empty, deleted.GetProperty("renterLastName").GetString());
        Assert.Equal(string.Empty, deleted.GetProperty("renterEmail").GetString());
        Assert.False(live.GetProperty("renterIsDeleted").GetBoolean());
        Assert.Equal("Live", live.GetProperty("renterFirstName").GetString());
    }

    [Fact]
    public async Task My_Bookings_Show_A_Deleted_Owner_As_Deleted()
    {
        var deletedOwner = await DeletedAsync("bk-mine");
        var liveOwner = await LiveAsync("bk-mine");
        var renter = await LiveAsync("bk-mine", home: false);
        var deletedListing = await ListingAsync(deletedOwner.Id, ListingStatus.Archived);
        var liveListing = await ListingAsync(liveOwner.Id);
        var withDeleted = await BookingAsync(deletedListing, renter.Id, BookingStatus.Completed);
        var withLive = await BookingAsync(liveListing, renter.Id, BookingStatus.Completed);

        var list = await JsonAsync(await ClientFor(renter).GetAsync("/api/bookings/mine"));

        var deleted = ById(list, "id", withDeleted);
        var live = ById(list, "id", withLive);
        Assert.True(deleted.GetProperty("ownerIsDeleted").GetBoolean());
        Assert.Equal(string.Empty, deleted.GetProperty("ownerFirstName").GetString());
        Assert.Equal(string.Empty, deleted.GetProperty("ownerLastName").GetString());
        Assert.False(live.GetProperty("ownerIsDeleted").GetBoolean());
        Assert.Equal("Live", live.GetProperty("ownerFirstName").GetString());
    }

    // ---- Chat ----------------------------------------------------------------------------------

    private async Task<Guid> ConversationAsync(Guid ownerId, Guid renterId, Guid bookingId, Guid? senderId)
    {
        var conversationId = Guid.NewGuid();
        await _factory.SeedAsync(TestData.Conversation(conversationId, bookingId, ownerId, renterId));
        if (senderId is { } sender)
        {
            await _factory.SeedAsync(TestData.ChatMessage(Guid.NewGuid(), conversationId, sender, "hi"));
        }

        return conversationId;
    }

    [Fact]
    public async Task Chat_List_And_Header_And_Messages_Show_A_Deleted_Counterpart_As_Deleted()
    {
        var owner = await LiveAsync("chat-read");
        var deletedRenter = await DeletedAsync("chat-read");
        var liveRenter = await LiveAsync("chat-read", home: false);
        var listingId = await ListingAsync(owner.Id);
        var bookingWithDeleted = await BookingAsync(listingId, deletedRenter.Id, BookingStatus.Cancelled);
        var bookingWithLive = await BookingAsync(listingId, liveRenter.Id, BookingStatus.Cancelled);
        var withDeleted = await ConversationAsync(owner.Id, deletedRenter.Id, bookingWithDeleted, deletedRenter.Id);
        var withLive = await ConversationAsync(owner.Id, liveRenter.Id, bookingWithLive, liveRenter.Id);
        var client = ClientFor(owner);

        var list = await JsonAsync(await client.GetAsync("/api/chat/conversations"));
        var deletedRow = ById(list, "id", withDeleted);
        var liveRow = ById(list, "id", withLive);
        Assert.True(deletedRow.GetProperty("counterpartIsDeleted").GetBoolean());
        Assert.Equal(string.Empty, deletedRow.GetProperty("counterpartName").GetString());
        Assert.Equal(JsonValueKind.Null, deletedRow.GetProperty("counterpartAvatarUrl").ValueKind);
        Assert.False(liveRow.GetProperty("counterpartIsDeleted").GetBoolean());
        Assert.Equal("Live Person", liveRow.GetProperty("counterpartName").GetString());

        // Reading the conversation still works.
        var header = await JsonAsync(await client.GetAsync($"/api/chat/conversations/{withDeleted}"));
        Assert.True(header.GetProperty("counterpartIsDeleted").GetBoolean());
        Assert.Equal(string.Empty, header.GetProperty("counterpartName").GetString());
        Assert.Equal(JsonValueKind.Null, header.GetProperty("counterpartAvatarUrl").ValueKind);
        Assert.False(header.GetProperty("counterpartVerified").GetBoolean());
        var message = Assert.Single(header.GetProperty("messages").EnumerateArray());
        Assert.True(message.GetProperty("senderIsDeleted").GetBoolean());
        Assert.Equal(string.Empty, message.GetProperty("senderName").GetString());
        Assert.Equal("hi", message.GetProperty("body").GetString());

        var liveHeader = await JsonAsync(await client.GetAsync($"/api/chat/conversations/{withLive}"));
        Assert.False(liveHeader.GetProperty("counterpartIsDeleted").GetBoolean());
        var liveMessage = Assert.Single(liveHeader.GetProperty("messages").EnumerateArray());
        Assert.False(liveMessage.GetProperty("senderIsDeleted").GetBoolean());
        Assert.Equal("Live Person", liveMessage.GetProperty("senderName").GetString());
    }

    [Fact]
    public async Task Sending_Into_A_Conversation_With_A_Deleted_Participant_Is_Refused_409_And_A_Live_One_Still_Works()
    {
        var owner = await LiveAsync("chat-send");
        var deletedRenter = await DeletedAsync("chat-send");
        var liveRenter = await LiveAsync("chat-send", home: false);
        var listingId = await ListingAsync(owner.Id);
        var bookingWithDeleted = await BookingAsync(listingId, deletedRenter.Id, BookingStatus.Cancelled);
        var bookingWithLive = await BookingAsync(listingId, liveRenter.Id, BookingStatus.Cancelled);
        var withDeleted = await ConversationAsync(owner.Id, deletedRenter.Id, bookingWithDeleted, senderId: null);
        var withLive = await ConversationAsync(owner.Id, liveRenter.Id, bookingWithLive, senderId: null);
        var client = ClientFor(owner);

        var refused = await client.PostAsJsonAsync("/api/chat/messages", new { conversationId = withDeleted, content = "hello?" });
        var allowed = await client.PostAsJsonAsync("/api/chat/messages", new { conversationId = withLive, content = "hello" });

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("chat.participant_deleted", (await JsonAsync(refused, HttpStatusCode.Conflict)).GetProperty("errorCode").GetString());
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        // Nothing was written into the refused thread.
        var header = await JsonAsync(await client.GetAsync($"/api/chat/conversations/{withDeleted}"));
        Assert.Empty(header.GetProperty("messages").EnumerateArray());
    }

    [Fact]
    public async Task A_Meeting_Point_Cannot_Be_Shared_With_A_Deleted_Participant()
    {
        var owner = await LiveAsync("chat-meeting");
        var deletedRenter = await DeletedAsync("chat-meeting");
        var listingId = await ListingAsync(owner.Id);
        var bookingId = await BookingAsync(listingId, deletedRenter.Id, BookingStatus.Approved);
        var conversationId = await ConversationAsync(owner.Id, deletedRenter.Id, bookingId, senderId: null);

        var response = await ClientFor(owner).PostAsJsonAsync(
            $"/api/chat/conversations/{conversationId}/meeting-point", new { kind = "home" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("chat.participant_deleted", (await JsonAsync(response, HttpStatusCode.Conflict)).GetProperty("errorCode").GetString());
    }

    // ---- Reviews, profile ----------------------------------------------------------------------

    [Fact]
    public async Task Review_Comments_Show_A_Deleted_Author_As_Deleted_On_The_Anonymous_Endpoints()
    {
        var owner = await LiveAsync("rev-author");
        var renter = await LiveAsync("rev-author", home: false);
        var deletedReviewer = await DeletedAsync("rev-author");
        var deletedOwnerForRenter = await DeletedAsync("rev-author-owner");
        var listingId = await ListingAsync(owner.Id);
        var byDeleted = await BookingAsync(listingId, deletedReviewer.Id, BookingStatus.Completed);
        var byLive = await BookingAsync(listingId, renter.Id, BookingStatus.Completed);
        var renterReviewedByDeletedOwner = await BookingAsync(listingId, renter.Id, BookingStatus.Completed);
        await _factory.SeedAsync(
            ToyReviewRow(byDeleted, listingId, deletedReviewer.Id, "from the gone one"),
            ToyReviewRow(byLive, listingId, renter.Id, "from the live one"),
            OwnerReviewRow(byDeleted, owner.Id, deletedReviewer.Id, "owner review by the gone one"),
            RenterReviewRow(renterReviewedByDeletedOwner, renter.Id, deletedOwnerForRenter.Id, "renter review by the gone owner"));
        var client = ClientFor();

        var toy = await JsonAsync(await client.GetAsync($"/api/reviews/listing/{listingId}"));
        var gone = toy.GetProperty("comments").EnumerateArray().Single(c => c.GetProperty("comment").GetString() == "from the gone one");
        var live = toy.GetProperty("comments").EnumerateArray().Single(c => c.GetProperty("comment").GetString() == "from the live one");
        Assert.True(gone.GetProperty("reviewerIsDeleted").GetBoolean());
        Assert.Equal(string.Empty, gone.GetProperty("reviewerFirstName").GetString());
        Assert.Equal(string.Empty, gone.GetProperty("reviewerLastName").GetString());
        Assert.Equal(JsonValueKind.Null, gone.GetProperty("reviewerAvatarUrl").ValueKind);
        Assert.False(live.GetProperty("reviewerIsDeleted").GetBoolean());
        Assert.Equal("Live", live.GetProperty("reviewerFirstName").GetString());
        Assert.Equal(LiveAvatar, live.GetProperty("reviewerAvatarUrl").GetString());

        var ownerSummary = await JsonAsync(await client.GetAsync($"/api/reviews/owner/{owner.Id}"));
        var ownerComment = Assert.Single(ownerSummary.GetProperty("comments").EnumerateArray());
        Assert.True(ownerComment.GetProperty("reviewerIsDeleted").GetBoolean());
        Assert.Equal(string.Empty, ownerComment.GetProperty("reviewerFirstName").GetString());

        var renterSummary = await JsonAsync(await client.GetAsync($"/api/reviews/renter/{renter.Id}"));
        var renterComment = Assert.Single(renterSummary.GetProperty("comments").EnumerateArray());
        Assert.True(renterComment.GetProperty("reviewerIsDeleted").GetBoolean());
        Assert.Equal(string.Empty, renterComment.GetProperty("reviewerFirstName").GetString());
    }

    [Fact]
    public async Task Owner_And_Renter_Review_Summaries_And_Profile_And_Public_Listings_Of_A_Deleted_User_Are_404()
    {
        var deleted = await DeletedAsync("gone-404");
        var live = await LiveAsync("gone-404");
        await ListingAsync(live.Id);
        var client = ClientFor();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/reviews/owner/{deleted.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/reviews/renter/{deleted.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/users/{deleted.Id}/public-profile")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/users/{deleted.Id}/listings")).StatusCode);

        // A live user is unaffected.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/reviews/owner/{live.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/reviews/renter/{live.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/users/{live.Id}/public-profile")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/users/{live.Id}/listings")).StatusCode);
    }

    [Fact]
    public async Task A_Renter_Can_Still_Review_The_Owner_And_The_Toy_Of_A_Completed_Rental_After_The_Owner_Deleted_Their_Account()
    {
        var deletedOwner = await DeletedAsync("rev-allowed-owner");
        var renter = await LiveAsync("rev-allowed-owner", home: false);
        var listingId = await ListingAsync(deletedOwner.Id, ListingStatus.Archived);
        var bookingId = await BookingAsync(listingId, renter.Id, BookingStatus.Completed);
        var client = ClientFor(renter);

        var owner = await client.PostAsJsonAsync("/api/reviews/owner", new
        {
            bookingId, communicationRating = 5, pickupHandoverRating = 4, friendlinessRating = 5, comment = "Lovely"
        });
        var toy = await client.PostAsJsonAsync("/api/reviews/toy", new
        {
            bookingId, overallRating = 5, conditionRating = 5, cleanlinessRating = 5, valueForMoneyRating = 5,
            funPlayValueRating = 5, descriptionAccuracyRating = 5, comment = "Fun"
        });

        Assert.Equal(HttpStatusCode.Created, owner.StatusCode);
        Assert.Equal(HttpStatusCode.Created, toy.StatusCode);
    }

    [Fact]
    public async Task An_Owner_Can_Still_Review_The_Renter_Of_A_Completed_Rental_After_The_Renter_Deleted_Their_Account()
    {
        var owner = await LiveAsync("rev-allowed-renter");
        var deletedRenter = await DeletedAsync("rev-allowed-renter");
        var listingId = await ListingAsync(owner.Id);
        var bookingId = await BookingAsync(listingId, deletedRenter.Id, BookingStatus.Completed);

        var response = await ClientFor(owner).PostAsJsonAsync("/api/reviews/renter", new
        {
            bookingId, communicationRating = 5, returnedOnTimeRating = 5, careOfToyRating = 4, wouldRentAgainRating = 5,
            comment = "Fine"
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    // ---- Admin ---------------------------------------------------------------------------------

    [Fact]
    public async Task Admin_User_List_And_Detail_Exclude_Deleted_Users()
    {
        var marker = $"Excl{Guid.NewGuid():N}";
        var admin = await LiveAsync("adm-users", UserRole.Admin, home: false);
        var live = await LiveAsync("adm-users", home: false, firstName: $"{marker}Live");
        var deleted = await DeletedAsync("adm-users", firstName: $"{marker}Gone");
        var client = ClientFor(admin);

        var list = await JsonAsync(await client.GetAsync($"/api/admin/users?status=all&search={marker}"));

        var ids = list.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ToList();
        Assert.Equal(new[] { live.Id }, ids);
        Assert.Equal(1, list.GetProperty("totalCount").GetInt32());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/admin/users/{live.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/admin/users/{deleted.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/api/admin/users/{deleted.Id}/suspend", content: null)).StatusCode);
    }

    [Fact]
    public async Task Admin_Report_Queue_Shows_A_Deleted_Reporter_As_Deleted()
    {
        var marker = $"Rep{Guid.NewGuid():N}";
        var admin = await LiveAsync("adm-reports", UserRole.Admin, home: false);
        var owner = await LiveAsync("adm-reports");
        var deletedReporter = await DeletedAsync("adm-reports", firstName: $"{marker}Gone");
        var liveReporter = await LiveAsync("adm-reports", home: false, firstName: $"{marker}Live");
        var listingId = await ListingAsync(owner.Id);
        var fromDeleted = Guid.NewGuid();
        var fromLive = Guid.NewGuid();
        await _factory.SeedAsync(
            TestData.Report(fromDeleted, ReportTargetType.Listing, listingId, deletedReporter.Id),
            TestData.Report(fromLive, ReportTargetType.Listing, listingId, liveReporter.Id));

        var queue = await JsonAsync(await ClientFor(admin).GetAsync($"/api/admin/reports?status=all&search={marker}"));

        var items = queue.GetProperty("items");
        var deleted = ById(items, "id", fromDeleted);
        var live = ById(items, "id", fromLive);
        Assert.True(deleted.GetProperty("reporterIsDeleted").GetBoolean());
        Assert.Equal(string.Empty, deleted.GetProperty("reporterFirstName").GetString());
        Assert.Equal(string.Empty, deleted.GetProperty("reporterLastName").GetString());
        Assert.Equal(string.Empty, deleted.GetProperty("reporterEmail").GetString());
        Assert.Equal(JsonValueKind.Null, deleted.GetProperty("reporterAvatarUrl").ValueKind);
        Assert.False(live.GetProperty("reporterIsDeleted").GetBoolean());
        Assert.StartsWith(marker, live.GetProperty("reporterFirstName").GetString());
    }

    [Fact]
    public async Task Admin_Message_Threads_Show_A_Deleted_Member_As_Deleted()
    {
        var marker = $"Thr{Guid.NewGuid():N}";
        var admin = await LiveAsync("adm-threads", UserRole.Admin, home: false);
        var deletedMember = await LiveAsync("adm-threads", home: false, firstName: $"{marker}Gone");
        var liveMember = await LiveAsync("adm-threads", home: false, firstName: $"{marker}Live");
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<RentalPlatform.Application.Abstractions.IConversationsStore>();
            var goneThread = await store.GetOrCreateForModerationAsync(admin.Id, deletedMember.Id);
            await store.AddTextMessageAsync(goneThread.Id, deletedMember.Id, "bye");
            var liveThread = await store.GetOrCreateForModerationAsync(admin.Id, liveMember.Id);
            await store.AddTextMessageAsync(liveThread.Id, liveMember.Id, "hi");
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Users.Where(u => u.Id == deletedMember.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.DeletedAt, DateTime.UtcNow));
        }

        var threads = await JsonAsync(await ClientFor(admin).GetAsync($"/api/admin/messages/threads?search={marker}"));

        var items = threads.GetProperty("items");
        var gone = ById(items, "memberId", deletedMember.Id);
        var live = ById(items, "memberId", liveMember.Id);
        Assert.True(gone.GetProperty("memberIsDeleted").GetBoolean());
        Assert.Equal(string.Empty, gone.GetProperty("memberFirstName").GetString());
        Assert.Equal(string.Empty, gone.GetProperty("memberLastName").GetString());
        Assert.Equal(JsonValueKind.Null, gone.GetProperty("memberAvatarUrl").ValueKind);
        Assert.False(live.GetProperty("memberIsDeleted").GetBoolean());

        // A moderator cannot open a new thread with a deleted member.
        var open = await ClientFor(admin).PostAsJsonAsync("/api/admin/messages/threads", new { userId = deletedMember.Id });
        Assert.Equal(HttpStatusCode.NotFound, open.StatusCode);
    }

    [Fact]
    public async Task Admin_Listing_Detail_Shows_A_Deleted_Owner_As_Deleted()
    {
        var admin = await LiveAsync("adm-listing", UserRole.Admin, home: false);
        var deletedOwner = await DeletedAsync("adm-listing");
        var liveOwner = await LiveAsync("adm-listing");
        var archivedByDeleted = await ListingAsync(deletedOwner.Id, ListingStatus.Archived);
        var approvedByLive = await ListingAsync(liveOwner.Id);
        var client = ClientFor(admin);

        var deleted = await JsonAsync(await client.GetAsync($"/api/admin/listings/{archivedByDeleted}"));
        var live = await JsonAsync(await client.GetAsync($"/api/admin/listings/{approvedByLive}"));

        Assert.True(deleted.GetProperty("ownerIsDeleted").GetBoolean());
        Assert.Equal(string.Empty, deleted.GetProperty("ownerFirstName").GetString());
        Assert.Equal(string.Empty, deleted.GetProperty("ownerLastName").GetString());
        Assert.Equal(string.Empty, deleted.GetProperty("ownerEmail").GetString());
        Assert.Equal(JsonValueKind.Null, deleted.GetProperty("ownerAvatarUrl").ValueKind);
        Assert.False(live.GetProperty("ownerIsDeleted").GetBoolean());
        Assert.Equal("Live", live.GetProperty("ownerFirstName").GetString());
    }

    private sealed class SpyEmailService : RentalPlatform.Application.Abstractions.IEmailService
    {
        public List<string> To { get; } = new();

        public Task SendListingApprovedAsync(string ownerEmail, string ownerName, string listingTitle, CancellationToken cancellationToken = default)
        { lock (To) { To.Add(ownerEmail); } return Task.CompletedTask; }

        public Task SendListingRejectedAsync(string ownerEmail, string ownerName, string listingTitle, string rejectionReason, CancellationToken cancellationToken = default)
        { lock (To) { To.Add(ownerEmail); } return Task.CompletedTask; }

        public Task SendEmailVerificationAsync(string email, string? preferredLanguage, string link, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SendPasswordResetAsync(string email, string? preferredLanguage, string link, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SendAccountDeletedAsync(string email, string? preferredLanguage, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task Moderating_A_Deleted_Owners_Pending_Listing_Sends_No_Email_And_Creates_No_Notification()
    {
        var admin = await LiveAsync("adm-moderate", UserRole.Admin, home: false);
        var deletedOwner = await DeletedAsync("adm-moderate");
        var liveOwner = await LiveAsync("adm-moderate");
        var forDeleted = await ListingAsync(deletedOwner.Id, ListingStatus.PendingApproval);
        var forLive = await ListingAsync(liveOwner.Id, ListingStatus.PendingApproval);
        var spy = new SpyEmailService();
        using var factory = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<RentalPlatform.Application.Abstractions.IEmailService>();
            services.AddSingleton<RentalPlatform.Application.Abstractions.IEmailService>(spy);
        }));
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", admin.Token);

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/admin/listings/{forDeleted}/approve", content: null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/admin/listings/{forLive}/approve", content: null)).StatusCode);

        Assert.Equal(new[] { liveOwner.Email }, spy.To);
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.Notifications.AnyAsync(n => n.RecipientId == deletedOwner.Id));
        Assert.True(await db.Notifications.AnyAsync(n => n.RecipientId == liveOwner.Id));
    }

    // ---- Catalogue -----------------------------------------------------------------------------

    [Fact]
    public async Task The_Catalogue_And_Listing_Detail_Never_Serve_A_Deleted_Owners_Listing_Even_If_It_Is_Approved()
    {
        var marker = $"Cat{Guid.NewGuid():N}";
        var deletedOwner = await DeletedAsync("catalogue");
        var liveOwner = await LiveAsync("catalogue");
        var byDeleted = await ListingAsync(deletedOwner.Id, ListingStatus.Approved, $"{marker} gone toy");
        var byLive = await ListingAsync(liveOwner.Id, ListingStatus.Approved, $"{marker} live toy");
        var client = ClientFor();

        var page = await JsonAsync(await client.GetAsync($"/api/listings?search={marker}"));

        var ids = page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ToList();
        Assert.Equal(new[] { byLive }, ids);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/listings/{byDeleted}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/listings/{byLive}")).StatusCode);
    }

    // ---- Seed rows -----------------------------------------------------------------------------

    private static ToyReview ToyReviewRow(Guid bookingId, Guid listingId, Guid reviewerId, string comment) => new()
    {
        Id = Guid.NewGuid(), BookingId = bookingId, ListingId = listingId, ReviewerId = reviewerId,
        OverallRating = 5, ConditionRating = 5, CleanlinessRating = 5, ValueForMoneyRating = 5,
        FunPlayValueRating = 5, DescriptionAccuracyRating = 5, Comment = comment, CreatedAt = DateTime.UtcNow
    };

    private static OwnerReview OwnerReviewRow(Guid bookingId, Guid ownerId, Guid reviewerId, string comment) => new()
    {
        Id = Guid.NewGuid(), BookingId = bookingId, OwnerId = ownerId, ReviewerId = reviewerId,
        CommunicationRating = 5, PickupHandoverRating = 5, FriendlinessRating = 5, Comment = comment,
        CreatedAt = DateTime.UtcNow
    };

    private static RenterReview RenterReviewRow(Guid bookingId, Guid renterId, Guid reviewerId, string comment) => new()
    {
        Id = Guid.NewGuid(), BookingId = bookingId, RenterId = renterId, ReviewerId = reviewerId,
        CommunicationRating = 5, ReturnedOnTimeRating = 5, CareOfToyRating = 5, WouldRentAgainRating = 5,
        Comment = comment, CreatedAt = DateTime.UtcNow
    };
}
