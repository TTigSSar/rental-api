using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using RentalPlatform.Application.DTOs;
using RentalPlatform.Application.Services;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Infrastructure.Persistence;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Auth;

// ADR-056 at the service level: the REAL AccountDeletionService and AccountDeletionStore over SQLite, with
// only the side-effect seams (notifications, chat lines, sessions, storage, email, budget, clock) as doubles.
// The endpoint, the JWT pipeline and the counterparty's view are in AccountDeletionHttpTests.
public sealed class AccountDeletionServiceTests
{
    private const string Password = "Secret1234";
    private static readonly Guid UserId = new("b1000000-0000-0000-0000-000000000001");
    private static readonly Guid OtherId = new("b1000000-0000-0000-0000-000000000002");
    private static readonly Guid CategoryId = new("b1000000-0000-0000-0000-000000000003");
    private static readonly Guid UserListingId = new("b1000000-0000-0000-0000-000000000004");
    private static readonly Guid OtherListingId = new("b1000000-0000-0000-0000-000000000005");
    private const string UserEmail = "gone.user@test.local";
    private const string AvatarUrl = "/uploads/listings/avatar/me.png";

    private static readonly DateOnly Today = TestData.Today;

    private sealed class Harness : IAsyncDisposable
    {
        public Harness(SqliteTestDatabase db, Guid? currentUserId, params IInterceptor[] interceptors)
        {
            Context = interceptors.Length == 0 ? db.CreateContext() : db.CreateContext(interceptors);
            Service = new AccountDeletionService(
                new FakeCurrentUserContext(currentUserId),
                new AccountDeletionStore(Context),
                new FakePasswordHasher(),
                Notifications,
                Chat,
                Revoker,
                Storage,
                Email,
                Budget,
                Clock);
        }

        public AppDbContext Context { get; }
        public AccountDeletionService Service { get; }
        public FakeNotificationEmitter Notifications { get; } = new();
        public FakeChatSystemMessageEmitter Chat { get; } = new();
        public FakeSessionRevoker Revoker { get; } = new();
        public FakeFileStorageService Storage { get; } = new();
        public FakeEmailService Email { get; } = new();
        public FakeEmailSendBudget Budget { get; } = new();
        public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 11, 6, 10, 0, 0, TimeSpan.Zero));

        public ValueTask DisposeAsync() => Context.DisposeAsync();
    }

    private static DeleteAccountRequest WithPassword(string? password = Password) => new() { Password = password };

    private static User Deleter(
        UserRole role = UserRole.User,
        string? passwordHash = null,
        bool blocked = false)
    {
        var user = TestData.OwnerWithHome(UserId, UserEmail, TestData.KentronPoint, isBlocked: blocked, preferredLanguage: "hy");
        user.Role = role;
        user.PasswordHash = passwordHash ?? $"hashed:{Password}";
        user.FirstName = "Gone";
        user.LastName = "Person";
        user.AvatarUrl = AvatarUrl;
        user.Location = "Yerevan, Armenia";
        user.ExternalAuthProvider = "google";
        user.ExternalProviderId = "g-sub-1";
        return user;
    }

    private static async Task SeedBaselineAsync(SqliteTestDatabase db, User? deleter = null)
    {
        await db.SeedAsync(
            deleter ?? Deleter(),
            TestData.OwnerWithHome(OtherId, "other.user@test.local", TestData.ArabkirPoint, preferredLanguage: "ru"),
            TestData.Category(CategoryId));
        await db.SeedAsync(
            TestData.Listing(UserListingId, UserId, CategoryId),
            TestData.Listing(OtherListingId, OtherId, CategoryId));
    }

    private static Booking Rental(Guid id, Guid listingId, Guid renterId, BookingStatus status, int offsetDays = 5) =>
        TestData.Booking(id, listingId, renterId, Today.AddDays(offsetDays), Today.AddDays(offsetDays + 2), status);

    private static async Task<User> UserAsync(SqliteTestDatabase db, Guid id = default)
    {
        await using var context = db.CreateContext();
        var key = id == default ? UserId : id;
        return await context.Users.AsNoTracking().SingleAsync(user => user.Id == key);
    }

    private static async Task<BookingStatus> StatusAsync(SqliteTestDatabase db, Guid bookingId)
    {
        await using var context = db.CreateContext();
        return (await context.Bookings.AsNoTracking().SingleAsync(booking => booking.Id == bookingId)).Status;
    }

    // ---- refusals: nothing changes ---------------------------------------------------------------------

    [Fact]
    public async Task An_Unauthenticated_Caller_Is_Refused()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);
        await using var h = new Harness(db, currentUserId: null);

        var result = await h.Service.DeleteCurrentUserAsync(WithPassword());

        Assert.Equal("auth.unauthenticated", result.Error!.Code);
    }

    [Fact]
    public async Task An_Already_Deleted_Or_Missing_Account_Is_Unauthenticated_And_Sends_Nothing()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);
        await using (var first = new Harness(db, UserId))
        {
            Assert.True((await first.Service.DeleteCurrentUserAsync(WithPassword())).IsSuccess);
        }

        await using var retry = new Harness(db, UserId);
        var again = await retry.Service.DeleteCurrentUserAsync(WithPassword());
        await using var ghost = new Harness(db, Guid.NewGuid());
        var missing = await ghost.Service.DeleteCurrentUserAsync(WithPassword());

        Assert.Equal("auth.unauthenticated", again.Error!.Code);
        Assert.Equal("auth.unauthenticated", missing.Error!.Code);
        Assert.Empty(retry.Email.AccountDeletedSent);
        Assert.Empty(retry.Revoker.Revoked);
        Assert.Equal(1, (await UserAsync(db)).TokenVersion); // rotated once, by the first deletion only
    }

    [Fact]
    public async Task An_Admin_Cannot_Delete_And_Nothing_Changes()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db, Deleter(UserRole.Admin));
        await using var h = new Harness(db, UserId);

        var result = await h.Service.DeleteCurrentUserAsync(WithPassword());

        Assert.Equal("account.admin_cannot_delete", result.Error!.Code);
        var stored = await UserAsync(db);
        Assert.Null(stored.DeletedAt);
        Assert.Equal(UserEmail, stored.Email);
        Assert.Empty(h.Email.AccountDeletedSent);
    }

    [Fact]
    public async Task The_Admin_Check_Comes_Before_The_Confirmation()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db, Deleter(UserRole.Admin));
        await using var h = new Harness(db, UserId);

        var result = await h.Service.DeleteCurrentUserAsync(WithPassword("definitely wrong"));

        Assert.Equal("account.admin_cannot_delete", result.Error!.Code);
    }

    [Theory]
    [InlineData("wrong-password")]
    [InlineData("")]
    [InlineData(null)]
    public async Task A_Wrong_Or_Missing_Password_Is_Invalid_Confirmation_And_Changes_Nothing(string? password)
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);
        await db.SeedAsync(Rental(Guid.NewGuid(), OtherListingId, UserId, BookingStatus.Pending));
        await using var h = new Harness(db, UserId);

        var result = await h.Service.DeleteCurrentUserAsync(new DeleteAccountRequest { Password = password, Email = UserEmail });

        Assert.Equal("account.invalid_confirmation", result.Error!.Code);
        var stored = await UserAsync(db);
        Assert.Null(stored.DeletedAt);
        Assert.Equal(0, stored.TokenVersion);
        Assert.Empty(h.Revoker.Revoked);
        Assert.Empty(h.Email.AccountDeletedSent);
    }

    [Fact]
    public async Task For_A_Password_Account_The_Email_Is_Not_Accepted_As_Confirmation()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);
        await using var h = new Harness(db, UserId);

        var result = await h.Service.DeleteCurrentUserAsync(new DeleteAccountRequest { Email = UserEmail });

        Assert.Equal("account.invalid_confirmation", result.Error!.Code);
    }

    [Theory]
    [InlineData("someone.else@test.local")]
    [InlineData("")]
    [InlineData(null)]
    // A collation-equal twin: SQL Server treats ss and the sharp s as equal, ordinal comparison does not (ADR-028 section 2).
    [InlineData("gone.user@teßt.local")]
    public async Task For_A_Passwordless_Account_A_Wrong_Email_Is_Invalid_Confirmation(string? email)
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db, Deleter(passwordHash: string.Empty));
        await using var h = new Harness(db, UserId);

        var result = await h.Service.DeleteCurrentUserAsync(new DeleteAccountRequest { Email = email, Password = "irrelevant" });

        Assert.Equal("account.invalid_confirmation", result.Error!.Code);
        Assert.Null((await UserAsync(db)).DeletedAt);
    }

    [Fact]
    public async Task For_A_Passwordless_Account_The_Right_Email_Deletes_Ignoring_Case_And_Padding()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db, Deleter(passwordHash: string.Empty));
        await using var h = new Harness(db, UserId);

        var result = await h.Service.DeleteCurrentUserAsync(new DeleteAccountRequest { Email = "  Gone.USER@test.local " });

        Assert.True(result.IsSuccess);
        Assert.NotNull((await UserAsync(db)).DeletedAt);
    }

    [Fact]
    public async Task A_Blocked_User_Can_Delete()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db, Deleter(blocked: true));
        await using var h = new Harness(db, UserId);

        var result = await h.Service.DeleteCurrentUserAsync(WithPassword());

        Assert.True(result.IsSuccess);
        Assert.NotNull((await UserAsync(db)).DeletedAt);
    }

    [Fact]
    public async Task An_Approved_Booking_As_Renter_Blocks_With_The_Open_Rental_Details_And_Changes_Nothing()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);
        var blocking = Guid.NewGuid();
        var pending = Guid.NewGuid();
        await db.SeedAsync(
            TestData.Image(Guid.NewGuid(), OtherListingId, isPrimary: true, sortOrder: 0),
            Rental(blocking, OtherListingId, UserId, BookingStatus.Approved, 10),
            Rental(pending, OtherListingId, UserId, BookingStatus.Pending, 30));
        await using var h = new Harness(db, UserId);

        var result = await h.Service.DeleteCurrentUserAsync(WithPassword());

        Assert.Equal("account.has_open_rentals", result.Error!.Code);
        var rentals = Assert.IsType<List<OpenRentalResponse>>(result.Error.Extensions!["openRentals"]);
        var rental = Assert.Single(rentals);
        Assert.Equal(blocking, rental.BookingId);
        Assert.Equal("LEGO Duplo Starter Set", rental.ListingTitle);
        Assert.StartsWith("/uploads/listings/", rental.ListingImageUrl);
        Assert.Equal(Today.AddDays(10), rental.StartDate);
        Assert.Equal(Today.AddDays(12), rental.EndDate);
        Assert.Equal("renter", rental.Role);

        Assert.Equal(BookingStatus.Pending, await StatusAsync(db, pending)); // the refusal rolled back; nothing closed
        Assert.Null((await UserAsync(db)).DeletedAt);
        Assert.Empty(h.Revoker.Revoked);
        Assert.Empty(h.Email.AccountDeletedSent);
    }

    [Fact]
    public async Task An_Active_Booking_As_Owner_Blocks_And_A_Listing_Without_Images_Has_A_Null_Image()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);
        await db.SeedAsync(Rental(Guid.NewGuid(), UserListingId, OtherId, BookingStatus.Active, 1));
        await using var h = new Harness(db, UserId);

        var result = await h.Service.DeleteCurrentUserAsync(WithPassword());

        Assert.Equal("account.has_open_rentals", result.Error!.Code);
        var rental = Assert.Single(Assert.IsType<List<OpenRentalResponse>>(result.Error.Extensions!["openRentals"]));
        Assert.Equal("owner", rental.Role);
        Assert.Null(rental.ListingImageUrl);
    }

    [Fact]
    public async Task Both_Roles_Are_Listed_And_Settled_Bookings_Do_Not_Block()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);
        await db.SeedAsync(
            Rental(Guid.NewGuid(), UserListingId, OtherId, BookingStatus.Approved, 3),
            Rental(Guid.NewGuid(), OtherListingId, UserId, BookingStatus.Active, 20),
            Rental(Guid.NewGuid(), OtherListingId, UserId, BookingStatus.Completed, -20),
            Rental(Guid.NewGuid(), OtherListingId, UserId, BookingStatus.Cancelled, 40),
            Rental(Guid.NewGuid(), OtherListingId, UserId, BookingStatus.Rejected, 50),
            Rental(Guid.NewGuid(), OtherListingId, UserId, BookingStatus.Expired, 60));
        await using var h = new Harness(db, UserId);

        var result = await h.Service.DeleteCurrentUserAsync(WithPassword());

        var rentals = Assert.IsType<List<OpenRentalResponse>>(result.Error!.Extensions!["openRentals"]);
        Assert.Equal(new[] { "owner", "renter" }, rentals.Select(rental => rental.Role).OrderBy(role => role).ToArray());
        Assert.Equal(2, rentals.Count);
    }

    // ---- Pending bookings are closed -------------------------------------------------------------------

    [Fact]
    public async Task Pending_Bookings_Are_Closed_Outgoing_Cancelled_And_Incoming_Rejected_And_The_Counterparty_Is_Told()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);
        var outgoing = Guid.NewGuid();
        var incoming = Guid.NewGuid();
        var completed = Guid.NewGuid();
        await db.SeedAsync(
            Rental(outgoing, OtherListingId, UserId, BookingStatus.Pending, 5),
            Rental(incoming, UserListingId, OtherId, BookingStatus.Pending, 9),
            Rental(completed, OtherListingId, UserId, BookingStatus.Completed, -30));
        await using var h = new Harness(db, UserId);

        var result = await h.Service.DeleteCurrentUserAsync(WithPassword());

        Assert.True(result.IsSuccess);
        Assert.Equal(BookingStatus.Cancelled, await StatusAsync(db, outgoing));
        Assert.Equal(BookingStatus.Rejected, await StatusAsync(db, incoming));
        Assert.Equal(BookingStatus.Completed, await StatusAsync(db, completed));

        // The renter of the incoming request is notified (actor "Deleted user" is rendered by the real emitter);
        // both counterparties get a chat line. All of it after the commit.
        var declined = Assert.Single(h.Notifications.DeclinedByDeletedOwner);
        Assert.Equal(incoming, declined.Id);
        Assert.Equal(BookingStatus.Rejected, declined.Status);
        Assert.Equal(new[] { "cancelled", "rejected" }, h.Chat.Calls.OrderBy(call => call).ToArray());
    }

    [Fact]
    public async Task A_Lost_Compare_And_Set_Rolls_Everything_Back_And_Answers_State_Changed()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        await db.SeedAsync(
            Rental(first, OtherListingId, UserId, BookingStatus.Pending, 5),
            Rental(second, OtherListingId, UserId, BookingStatus.Pending, 20));

        // Right before the SECOND close-UPDATE the owner approves what is still Pending: that CAS loses, after
        // the first one already won, so the rollback has something to undo.
        var interceptor = new ApproveStillPendingBeforeNthBookingsUpdate(db, nth: 2);
        await using var h = new Harness(db, UserId, interceptor);

        var result = await h.Service.DeleteCurrentUserAsync(WithPassword());

        Assert.Equal("account.state_changed", result.Error!.Code);
        Assert.True(interceptor.Raced);
        var stored = await UserAsync(db);
        Assert.Null(stored.DeletedAt);
        Assert.Equal(UserEmail, stored.Email);
        Assert.Equal(0, stored.TokenVersion);
        // Both bookings are as they were before the attempt (the competing approve was part of the same SQLite
        // connection transaction, so it is undone too; what matters is that the first close is undone).
        Assert.Equal(BookingStatus.Pending, await StatusAsync(db, first));
        Assert.Equal(BookingStatus.Pending, await StatusAsync(db, second));
        Assert.Equal(ListingStatus.Approved, await ListingStatusAsync(db, UserListingId));
        Assert.Empty(h.Revoker.Revoked);
        Assert.Empty(h.Email.AccountDeletedSent);
        Assert.Empty(h.Chat.Calls);
    }

    private static async Task<ListingStatus> ListingStatusAsync(SqliteTestDatabase db, Guid listingId)
    {
        await using var context = db.CreateContext();
        return (await context.Listings.AsNoTracking().SingleAsync(listing => listing.Id == listingId)).Status;
    }

    private sealed class ApproveStillPendingBeforeNthBookingsUpdate : DbCommandInterceptor
    {
        private readonly SqliteTestDatabase _db;
        private readonly int _nth;
        private int _seen;

        public ApproveStillPendingBeforeNthBookingsUpdate(SqliteTestDatabase db, int nth)
        {
            _db = db;
            _nth = nth;
        }

        public bool Raced { get; private set; }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            System.Data.Common.DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains("\"Bookings\"")
                && ++_seen == _nth)
            {
                Raced = true;
                await using var other = _db.CreateContext();
                await other.Bookings
                    .Where(booking => booking.Status == BookingStatus.Pending)
                    .ExecuteUpdateAsync(set => set.SetProperty(booking => booking.Status, BookingStatus.Approved), cancellationToken);
            }

            return result;
        }
    }

    // ---- the erasure -----------------------------------------------------------------------------------

    [Fact]
    public async Task Deletion_Erases_The_Personal_Data_And_Anonymizes_The_Row()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);
        await using var h = new Harness(db, UserId);

        var result = await h.Service.DeleteCurrentUserAsync(WithPassword());

        Assert.True(result.IsSuccess);
        var stored = await UserAsync(db);
        Assert.Equal(h.Clock.GetUtcNow().UtcDateTime, stored.DeletedAt);
        Assert.Equal($"deleted-{UserId}@deleted.invalid", stored.Email);
        Assert.Equal(string.Empty, stored.FirstName);
        Assert.Equal(string.Empty, stored.LastName);
        Assert.Equal(string.Empty, stored.PasswordHash);
        Assert.Null(stored.PhoneNumber);
        Assert.Null(stored.AvatarUrl);
        Assert.Null(stored.Location);
        Assert.Null(stored.PreferredLanguage);
        Assert.Null(stored.ExternalAuthProvider);
        Assert.Null(stored.ExternalProviderId);
        Assert.Null(stored.HomeLatitude);
        Assert.Null(stored.HomeLongitude);
        Assert.Null(stored.HomePublicLatitude);
        Assert.Null(stored.HomePublicLongitude);
        Assert.Null(stored.HomeDistrictId);
        Assert.Null(stored.HomePointUpdatedAt);
        Assert.Equal(1, stored.TokenVersion);
        Assert.Equal(UserRole.User, stored.Role);
    }

    [Fact]
    public async Task Deletion_Archives_The_Listings_And_Clears_Their_Locations_And_Snapshots()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);
        var second = Guid.NewGuid();
        await using (var seed = db.CreateContext())
        {
            foreach (var listing in await seed.Listings.Where(candidate => candidate.OwnerId == UserId || candidate.OwnerId == OtherId).ToListAsync())
            {
                listing.AddressLine = "1 Secret Street";
                listing.Latitude = 40.1856m;
                listing.Longitude = 44.5126m;
                listing.PublicLatitude = 40.18m;
                listing.PublicLongitude = 44.51m;
            }

            seed.Listings.Add(TestData.Listing(second, UserId, CategoryId, ListingStatus.Draft));
            seed.ListingLocationsBeforeHomePoint.AddRange(
                new ListingLocationBeforeHomePoint { ListingId = UserListingId, Latitude = 40.1m, Longitude = 44.5m, City = "Yerevan", Country = "Armenia", CapturedAt = DateTime.UtcNow },
                new ListingLocationBeforeHomePoint { ListingId = OtherListingId, Latitude = 40.2m, Longitude = 44.6m, City = "Yerevan", Country = "Armenia", CapturedAt = DateTime.UtcNow });
            await seed.SaveChangesAsync();
        }

        await using var h = new Harness(db, UserId);
        Assert.True((await h.Service.DeleteCurrentUserAsync(WithPassword())).IsSuccess);

        await using var verify = db.CreateContext();
        var mine = await verify.Listings.AsNoTracking().Where(listing => listing.OwnerId == UserId).ToListAsync();
        Assert.Equal(2, mine.Count);
        Assert.All(mine, listing =>
        {
            Assert.Equal(ListingStatus.Archived, listing.Status);
            Assert.Null(listing.AddressLine);
            Assert.Null(listing.Latitude);
            Assert.Null(listing.Longitude);
            Assert.Null(listing.PublicLatitude);
            Assert.Null(listing.PublicLongitude);
        });

        var others = await verify.Listings.AsNoTracking().SingleAsync(listing => listing.Id == OtherListingId);
        Assert.Equal(ListingStatus.Approved, others.Status);
        Assert.Equal("1 Secret Street", others.AddressLine);
        Assert.Equal(40.1856m, others.Latitude);

        var snapshot = await verify.ListingLocationsBeforeHomePoint.AsNoTracking().SingleAsync();
        Assert.Equal(OtherListingId, snapshot.ListingId);
    }

    [Fact]
    public async Task Deletion_Removes_Favorites_Own_Notifications_And_Tokens_And_Keeps_Other_Peoples()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);
        var othersFavorite = Guid.NewGuid();
        await db.SeedAsync(
            new Favorite { Id = Guid.NewGuid(), UserId = UserId, ListingId = OtherListingId, CreatedAt = DateTime.UtcNow },
            new Favorite { Id = othersFavorite, UserId = OtherId, ListingId = UserListingId, CreatedAt = DateTime.UtcNow },
            Notice(UserId, NotificationEntityType.Listing, UserListingId, system: true),
            Notice(UserId, NotificationEntityType.Booking, Guid.NewGuid()),
            new UserToken { Id = Guid.NewGuid(), UserId = UserId, Purpose = TokenPurpose.PasswordReset, TokenHash = new byte[32], CreatedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddMinutes(30) },
            new UserToken { Id = Guid.NewGuid(), UserId = OtherId, Purpose = TokenPurpose.PasswordReset, TokenHash = new byte[] { 1 }, CreatedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddMinutes(30) });
        await using var h = new Harness(db, UserId);

        Assert.True((await h.Service.DeleteCurrentUserAsync(WithPassword())).IsSuccess);

        await using var verify = db.CreateContext();
        Assert.Equal(othersFavorite, (await verify.Favorites.AsNoTracking().SingleAsync()).Id);
        Assert.Empty(await verify.Notifications.AsNoTracking().Where(notification => notification.RecipientId == UserId).ToListAsync());
        Assert.Equal(OtherId, (await verify.UserTokens.AsNoTracking().SingleAsync()).UserId);
    }

    private static Notification Notice(
        Guid recipientId, NotificationEntityType entityType, Guid entityId, bool system = false) => new()
    {
        Id = Guid.NewGuid(),
        RecipientId = recipientId,
        Kind = NotificationKind.Request,
        Category = NotificationCategory.Booking,
        Title = "Gone requested your toy",
        Body = "Gone Person wants to rent it",
        ActorName = system ? "DoRent" : "Gone Person",
        ActorIsSystem = system,
        EntityType = entityType,
        EntityId = entityId,
        DeepLink = "/x",
        CreatedAt = DateTime.UtcNow
    };

    [Fact]
    public async Task Other_Recipients_Non_System_Notifications_On_Entities_The_User_Took_Part_In_Are_Deleted()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);
        var theirBooking = Guid.NewGuid();
        var unrelatedBooking = Guid.NewGuid();
        var thirdListing = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var otherConversationId = Guid.NewGuid();
        var reviewId = Guid.NewGuid();
        await db.SeedAsync(TestData.Listing(thirdListing, OtherId, CategoryId));
        await db.SeedAsync(
            Rental(theirBooking, OtherListingId, UserId, BookingStatus.Completed, -20),
            Rental(unrelatedBooking, thirdListing, OtherId, BookingStatus.Completed, -40));
        await db.SeedAsync(
            TestData.Conversation(conversationId, theirBooking, OtherId, UserId),
            TestData.Conversation(otherConversationId, unrelatedBooking, OtherId, OtherId));
        await db.SeedAsync(new OwnerReview
        {
            Id = reviewId, BookingId = theirBooking, OwnerId = OtherId, ReviewerId = UserId,
            CommunicationRating = 5, PickupHandoverRating = 5, FriendlinessRating = 5, CreatedAt = DateTime.UtcNow
        });

        var onBooking = Notice(OtherId, NotificationEntityType.Booking, theirBooking);
        var onConversation = Notice(OtherId, NotificationEntityType.Message, conversationId);
        var onReview = Notice(OtherId, NotificationEntityType.Review, reviewId);
        var systemOnBooking = Notice(OtherId, NotificationEntityType.Booking, theirBooking, system: true);
        var onUnrelatedBooking = Notice(OtherId, NotificationEntityType.Booking, unrelatedBooking);
        var onUnrelatedConversation = Notice(OtherId, NotificationEntityType.Message, otherConversationId);
        var onOthersListing = Notice(OtherId, NotificationEntityType.Listing, OtherListingId);
        await db.SeedAsync(onBooking, onConversation, onReview, systemOnBooking, onUnrelatedBooking, onUnrelatedConversation, onOthersListing);
        await using var h = new Harness(db, UserId);

        Assert.True((await h.Service.DeleteCurrentUserAsync(WithPassword())).IsSuccess);

        await using var verify = db.CreateContext();
        var survivors = (await verify.Notifications.AsNoTracking().ToListAsync()).Select(notification => notification.Id).ToHashSet();
        Assert.DoesNotContain(onBooking.Id, survivors);
        Assert.DoesNotContain(onConversation.Id, survivors);
        Assert.DoesNotContain(onReview.Id, survivors);
        Assert.Contains(systemOnBooking.Id, survivors);
        Assert.Contains(onUnrelatedBooking.Id, survivors);
        Assert.Contains(onUnrelatedConversation.Id, survivors);
        Assert.Contains(onOthersListing.Id, survivors);
        Assert.Equal(4, survivors.Count);
    }

    [Fact]
    public async Task Meeting_Point_Coordinates_Are_Erased_In_Sent_Messages_And_The_Rows_Stay()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);
        var bookingId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var mine = Guid.NewGuid();
        var theirs = Guid.NewGuid();
        var text = Guid.NewGuid();
        await db.SeedAsync(Rental(bookingId, UserListingId, OtherId, BookingStatus.Completed, -20));
        await db.SeedAsync(TestData.Conversation(conversationId, bookingId, UserId, OtherId));
        var pointByMe = TestData.ChatMessage(mine, conversationId, UserId, null, MessageType.Location);
        pointByMe.MeetingPointLatitude = 40.1856m;
        pointByMe.MeetingPointLongitude = 44.5126m;
        pointByMe.MeetingPointKind = MeetingPointKind.Home;
        var pointByThem = TestData.ChatMessage(theirs, conversationId, OtherId, null, MessageType.Location);
        pointByThem.MeetingPointLatitude = 40.2m;
        pointByThem.MeetingPointLongitude = 44.6m;
        pointByThem.MeetingPointKind = MeetingPointKind.Other;
        await db.SeedAsync(pointByMe, pointByThem, TestData.ChatMessage(text, conversationId, UserId, "see you"));
        await using var h = new Harness(db, UserId);

        Assert.True((await h.Service.DeleteCurrentUserAsync(WithPassword())).IsSuccess);

        await using var verify = db.CreateContext();
        var messages = await verify.ChatMessages.AsNoTracking().ToDictionaryAsync(message => message.Id);
        Assert.Equal(3, messages.Count);
        Assert.Null(messages[mine].MeetingPointLatitude);
        Assert.Null(messages[mine].MeetingPointLongitude);
        Assert.Equal(MessageType.Location, messages[mine].Type);
        Assert.Equal(40.2m, messages[theirs].MeetingPointLatitude);
        Assert.Equal(44.6m, messages[theirs].MeetingPointLongitude);
        Assert.Equal("see you", messages[text].Body);
    }

    [Fact]
    public async Task Search_Logs_Lose_The_User_And_Admin_Labels_Lose_The_Name()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);
        var adminId = Guid.NewGuid();
        var keptLog = Guid.NewGuid();
        var userLog = Guid.NewGuid();
        var userReport = Guid.NewGuid();
        var listingReport = Guid.NewGuid();
        var chatReport = Guid.NewGuid();
        var otherChatReport = Guid.NewGuid();
        var userEntry = Guid.NewGuid();
        var listingEntry = Guid.NewGuid();
        await db.SeedAsync(TestData.User(adminId, "admin@test.local", role: UserRole.Admin));
        await db.SeedAsync(
            new AiSearchLog { Id = userLog, UserId = UserId, CreatedAt = DateTime.UtcNow, Outcome = "ok", CostUsd = 0.01m },
            new AiSearchLog { Id = keptLog, UserId = OtherId, CreatedAt = DateTime.UtcNow, Outcome = "ok", CostUsd = 0.02m },
            TestData.Report(userReport, ReportTargetType.User, UserId, OtherId, targetLabel: "Gone Person"),
            TestData.Report(listingReport, ReportTargetType.Listing, UserListingId, OtherId, targetLabel: "LEGO Duplo Starter Set"),
            TestData.Report(chatReport, ReportTargetType.Message, Guid.NewGuid(), OtherId, targetLabel: "Chat with Gone Person"),
            TestData.Report(otherChatReport, ReportTargetType.Message, Guid.NewGuid(), UserId, targetLabel: "Chat with Test User"),
            new ModerationLogEntry { Id = userEntry, ActorUserId = adminId, Action = ModerationAction.UserSuspended, TargetType = ModerationTargetType.User, TargetId = UserId, TargetLabel = "Gone Person", CreatedAt = DateTime.UtcNow },
            new ModerationLogEntry { Id = listingEntry, ActorUserId = adminId, Action = ModerationAction.ListingApproved, TargetType = ModerationTargetType.Listing, TargetId = UserListingId, TargetLabel = "LEGO Duplo Starter Set", CreatedAt = DateTime.UtcNow });
        await using var h = new Harness(db, UserId);

        Assert.True((await h.Service.DeleteCurrentUserAsync(WithPassword())).IsSuccess);

        await using var verify = db.CreateContext();
        Assert.Null((await verify.AiSearchLogs.AsNoTracking().SingleAsync(log => log.Id == userLog)).UserId);
        Assert.Equal(OtherId, (await verify.AiSearchLogs.AsNoTracking().SingleAsync(log => log.Id == keptLog)).UserId);
        var labels = await verify.Reports.AsNoTracking().ToDictionaryAsync(report => report.Id, report => report.TargetLabel);
        Assert.Equal("Deleted user", labels[userReport]);
        Assert.Equal("LEGO Duplo Starter Set", labels[listingReport]);
        Assert.Equal("Chat with Deleted user", labels[chatReport]);
        Assert.Equal("Chat with Test User", labels[otherChatReport]);
        var entries = await verify.ModerationLogEntries.AsNoTracking().ToDictionaryAsync(entry => entry.Id, entry => entry.TargetLabel);
        Assert.Equal("Deleted user", entries[userEntry]);
        Assert.Equal("LEGO Duplo Starter Set", entries[listingEntry]);
    }

    [Fact]
    public async Task Bookings_Messages_And_Reviews_Stay_For_The_Counterparty()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);
        var bookingId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var reviewId = Guid.NewGuid();
        await db.SeedAsync(Rental(bookingId, OtherListingId, UserId, BookingStatus.Completed, -20));
        await db.SeedAsync(TestData.Conversation(conversationId, bookingId, OtherId, UserId));
        await db.SeedAsync(
            TestData.ChatMessage(messageId, conversationId, UserId, "thanks!"),
            new OwnerReview { Id = reviewId, BookingId = bookingId, OwnerId = OtherId, ReviewerId = UserId, CommunicationRating = 4, PickupHandoverRating = 4, FriendlinessRating = 4, Comment = "Great", CreatedAt = DateTime.UtcNow });
        await using var h = new Harness(db, UserId);

        Assert.True((await h.Service.DeleteCurrentUserAsync(WithPassword())).IsSuccess);

        await using var verify = db.CreateContext();
        Assert.Equal(BookingStatus.Completed, (await verify.Bookings.AsNoTracking().SingleAsync(booking => booking.Id == bookingId)).Status);
        Assert.Equal("thanks!", (await verify.ChatMessages.AsNoTracking().SingleAsync(message => message.Id == messageId)).Body);
        Assert.Equal("Great", (await verify.OwnerReviews.AsNoTracking().SingleAsync(review => review.Id == reviewId)).Comment);
    }

    // ---- after the commit ------------------------------------------------------------------------------

    [Fact]
    public async Task After_The_Commit_Sessions_Are_Revoked_The_Avatar_Is_Deleted_And_The_Email_Goes_To_The_Old_Address_In_The_Old_Language()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);
        await using var h = new Harness(db, UserId);

        Assert.True((await h.Service.DeleteCurrentUserAsync(WithPassword())).IsSuccess);

        Assert.Equal(new[] { UserId }, h.Revoker.Revoked);
        Assert.Equal(new[] { AvatarUrl }, h.Storage.DeletedUrls);
        var sent = Assert.Single(h.Email.AccountDeletedSent);
        Assert.Equal(UserEmail, sent.Email);
        Assert.Equal("hy", sent.Language);
        Assert.Equal(1, h.Budget.Acquired); // counts against the send budget
    }

    [Fact]
    public async Task A_Throwing_Email_Service_Does_Not_Fail_The_Committed_Deletion()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);
        await using var h = new Harness(db, UserId);
        h.Email.ThrowOnAccountDeleted = true;

        var result = await h.Service.DeleteCurrentUserAsync(WithPassword());

        Assert.True(result.IsSuccess);
        Assert.NotNull((await UserAsync(db)).DeletedAt);
        Assert.Single(h.Email.AccountDeletedSent);
    }

    [Fact]
    public async Task An_Exhausted_Send_Budget_Skips_The_Email_But_Not_The_Deletion()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);
        await using var h = new Harness(db, UserId);
        h.Budget.Remaining = 0;

        var result = await h.Service.DeleteCurrentUserAsync(WithPassword());

        Assert.True(result.IsSuccess);
        Assert.Empty(h.Email.AccountDeletedSent);
        Assert.NotNull((await UserAsync(db)).DeletedAt);
    }

    [Fact]
    public async Task An_Account_Without_An_Avatar_Deletes_No_File()
    {
        using var db = new SqliteTestDatabase();
        var deleter = Deleter();
        deleter.AvatarUrl = null;
        await SeedBaselineAsync(db, deleter);
        await using var h = new Harness(db, UserId);

        Assert.True((await h.Service.DeleteCurrentUserAsync(WithPassword())).IsSuccess);

        Assert.Empty(h.Storage.DeletedUrls);
    }
}
