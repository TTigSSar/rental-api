using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Infrastructure.Persistence;
using RentalPlatform.Infrastructure.Services;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Infrastructure;

// ADR-056 (AD-3): nothing is created for a deleted recipient. The deletion erases the user's own notifications in
// its transaction; these guards close the window of an emit that runs after it (a booking action, a moderation
// decision, a home-point change fanning out to renters). The AD-2 Pending-close notifications to LIVE
// counterparties must keep working.
public sealed class DeletedRecipientGuardTests
{
    private static readonly Guid OwnerId = new("e7000000-0000-0000-0000-000000000001");
    private static readonly Guid RenterId = new("e7000000-0000-0000-0000-000000000002");
    private static readonly Guid CategoryId = new("e7000000-0000-0000-0000-000000000003");
    private static readonly Guid ListingId = new("e7000000-0000-0000-0000-000000000004");

    private static User Deleted(Guid id, string email)
    {
        var user = TestData.User(id, email);
        user.DeletedAt = DateTime.UtcNow;
        return user;
    }

    private static (Listing Listing, Booking Booking) Rental(User owner, User renter)
    {
        var listing = TestData.Listing(ListingId, owner.Id, CategoryId);
        listing.Owner = owner;
        var booking = TestData.Booking(
            Guid.NewGuid(), ListingId, renter.Id, TestData.Today.AddDays(2), TestData.Today.AddDays(4), BookingStatus.Pending);
        booking.Listing = listing;
        booking.Renter = renter;
        return (listing, booking);
    }

    [Fact]
    public async Task A_Request_Notification_To_A_Deleted_Owner_Is_Not_Created_But_One_To_A_Live_Owner_Is()
    {
        using var db = new SqliteTestDatabase();
        var deletedOwner = Deleted(OwnerId, "gone-owner@test.local");
        var liveRenter = TestData.User(RenterId, "renter@test.local");
        await db.SeedAsync(deletedOwner, liveRenter);
        var (listing, booking) = Rental(deletedOwner, liveRenter);
        await using var context = db.CreateContext();
        var emitter = new NotificationEmitter(new NotificationsStore(context), NullLogger<NotificationEmitter>.Instance);

        await emitter.BookingRequestedAsync(booking, liveRenter, listing);
        Assert.Empty(context.Notifications);

        // Control: the same emit to a live owner is created.
        var liveOwner = TestData.User(Guid.NewGuid(), "live-owner@test.local");
        await db.SeedAsync(liveOwner);
        var (liveListing, liveBooking) = Rental(liveOwner, liveRenter);
        liveListing.OwnerId = liveOwner.Id;
        await emitter.BookingRequestedAsync(liveBooking, liveRenter, liveListing);
        var created = Assert.Single(context.Notifications);
        Assert.Equal(liveOwner.Id, created.RecipientId);
    }

    [Fact]
    public async Task Approved_Declined_And_Pickup_Notifications_To_A_Deleted_Renter_Are_Not_Created()
    {
        using var db = new SqliteTestDatabase();
        var liveOwner = TestData.User(OwnerId, "owner@test.local");
        var deletedRenter = Deleted(RenterId, "gone-renter@test.local");
        await db.SeedAsync(liveOwner, deletedRenter);
        var (listing, booking) = Rental(liveOwner, deletedRenter);
        await using var context = db.CreateContext();
        var emitter = new NotificationEmitter(new NotificationsStore(context), NullLogger<NotificationEmitter>.Instance);

        await emitter.BookingApprovedAsync(booking, liveOwner);
        await emitter.BookingDeclinedAsync(booking, liveOwner);
        await emitter.BookingDeclinedByDeletedOwnerAsync(booking);
        await emitter.PickupAreaChangedAsync(booking, liveOwner, newDistrict: null);

        Assert.Empty(context.Notifications);
        Assert.NotNull(listing);
    }

    [Fact]
    public async Task The_Pending_Close_Notification_From_A_Deleted_Owner_Still_Reaches_A_Live_Renter()
    {
        using var db = new SqliteTestDatabase();
        var deletedOwner = Deleted(OwnerId, "gone-owner@test.local");
        var liveRenter = TestData.User(RenterId, "renter@test.local");
        await db.SeedAsync(deletedOwner, liveRenter);
        var (_, booking) = Rental(deletedOwner, liveRenter);
        await using var context = db.CreateContext();
        var emitter = new NotificationEmitter(new NotificationsStore(context), NullLogger<NotificationEmitter>.Instance);

        await emitter.BookingDeclinedByDeletedOwnerAsync(booking);

        var notification = Assert.Single(context.Notifications);
        Assert.Equal(RenterId, notification.RecipientId);
        Assert.Equal(NotificationKind.Declined, notification.Kind);
    }

    [Fact]
    public async Task A_Moderation_Note_For_A_Deleted_Member_Is_Not_Created()
    {
        using var db = new SqliteTestDatabase();
        var admin = TestData.User(OwnerId, "admin@test.local", role: UserRole.Admin);
        var deletedMember = Deleted(RenterId, "gone-member@test.local");
        var liveMember = TestData.User(Guid.NewGuid(), "live-member@test.local");
        await db.SeedAsync(admin, deletedMember, liveMember);
        await using var context = db.CreateContext();
        var emitter = new ModerationNoteEmitter(
            new ConversationsStore(context, NullLogger<ConversationsStore>.Instance),
            new FakeChatRealtimeNotifier(),
            NullLogger<ModerationNoteEmitter>.Instance);

        await emitter.AccountSuspendedAsync(admin.Id, deletedMember.Id, "en", "Spam");
        Assert.Empty(context.Conversations);
        Assert.Empty(context.ChatMessages);

        // Control: a live member still receives the note.
        await emitter.AccountSuspendedAsync(admin.Id, liveMember.Id, "en", "Spam");
        Assert.Single(await context.Conversations.ToListAsync());
        Assert.Single(await context.ChatMessages.ToListAsync());
    }
}
