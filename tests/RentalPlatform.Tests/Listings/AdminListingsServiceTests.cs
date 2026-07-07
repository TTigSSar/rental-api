using RentalPlatform.Application.Services;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Infrastructure.Persistence;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Listings;

// Admin moderation rules: role enforcement, valid status transitions, and owner notification.
public sealed class AdminListingsServiceTests
{
    private static readonly Guid AdminId = new("d0000000-0000-0000-0000-000000000001");
    private static readonly Guid OwnerId = new("d0000000-0000-0000-0000-000000000002");
    private static readonly Guid CategoryId = new("d0000000-0000-0000-0000-000000000003");
    private static readonly Guid ListingId = new("d0000000-0000-0000-0000-000000000004");

    private static async Task SeedAsync(SqliteTestDatabase db, ListingStatus listingStatus)
    {
        await db.SeedAsync(
            TestData.User(AdminId, "admin@test.local", role: UserRole.Admin),
            TestData.User(OwnerId, "owner@test.local"),
            TestData.Category(CategoryId));

        await db.SeedAsync(TestData.Listing(ListingId, OwnerId, CategoryId, listingStatus));
    }

    private static AdminListingsService CreateService(
        AppDbContext context, Guid currentUserId, FakeEmailService email) =>
        new(new FakeCurrentUserContext(currentUserId), new AdminListingsStore(context), email, new FakeNotificationEmitter());

    [Fact]
    public async Task Approve_Pending_Listing_Sets_Approved_And_Notifies_Owner()
    {
        using var db = new SqliteTestDatabase();
        await SeedAsync(db, ListingStatus.PendingApproval);
        var email = new FakeEmailService();

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId, email).ApproveAsync(ListingId);

        Assert.True(result.IsSuccess);
        Assert.Equal(ListingStatus.Approved, result.Value!.Status);

        await using var verify = db.CreateContext();
        var stored = await verify.Listings.FindAsync(ListingId);
        Assert.Equal(ListingStatus.Approved, stored!.Status);
        Assert.Equal(AdminId, stored.ModeratedByUserId);
        Assert.Single(email.ApprovedSent);
    }

    [Fact]
    public async Task Reject_Pending_Listing_Stores_Reason_And_Notifies_Owner()
    {
        using var db = new SqliteTestDatabase();
        await SeedAsync(db, ListingStatus.PendingApproval);
        var email = new FakeEmailService();

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId, email).RejectAsync(ListingId, "unsafeItem", "  Small parts.  ");

        Assert.True(result.IsSuccess);
        Assert.Equal(ListingStatus.Rejected, result.Value!.Status);

        await using var verify = db.CreateContext();
        var stored = await verify.Listings.FindAsync(ListingId);
        Assert.Equal(ListingStatus.Rejected, stored!.Status);
        Assert.Equal("unsafeItem", stored.RejectionReasonCode);
        Assert.Equal("Small parts.", stored.RejectionNote); // trimmed
        Assert.Equal("Unsafe item: Small parts.", stored.RejectionReason); // composed label + note
        Assert.Single(email.RejectedSent);
    }

    [Fact]
    public async Task Approve_By_Non_Admin_Is_Forbidden()
    {
        using var db = new SqliteTestDatabase();
        await SeedAsync(db, ListingStatus.PendingApproval);
        var email = new FakeEmailService();

        await using var context = db.CreateContext();
        var result = await CreateService(context, OwnerId, email).ApproveAsync(ListingId);

        Assert.False(result.IsSuccess);
        Assert.Equal("admin.forbidden", result.Error!.Code);
        Assert.Empty(email.ApprovedSent);
    }

    [Theory]
    [InlineData(ListingStatus.Approved)]
    [InlineData(ListingStatus.Rejected)]
    [InlineData(ListingStatus.Archived)]
    public async Task Approve_Non_Pending_Listing_Fails(ListingStatus status)
    {
        using var db = new SqliteTestDatabase();
        await SeedAsync(db, status);
        var email = new FakeEmailService();

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId, email).ApproveAsync(ListingId);

        Assert.False(result.IsSuccess);
        Assert.Equal("admin.invalid_listing_status", result.Error!.Code);
    }
}
