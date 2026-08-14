using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
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
        new(
            new FakeCurrentUserContext(currentUserId),
            new AdminListingsStore(context),
            new ReviewsStore(context),
            new ModerationLogStore(context, NullLogger<ModerationLogStore>.Instance),
            email,
            new FakeNotificationEmitter());

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

    // ---------- MUST-FIX 1: DetailJson must not overflow its 2000-char column for non-ASCII notes ----------

    [Fact]
    public async Task Reject_With_A_Full_Length_Armenian_Note_Produces_A_Bounded_Valid_DetailJson()
    {
        using var db = new SqliteTestDatabase();
        await SeedAsync(db, ListingStatus.PendingApproval);
        var email = new FakeEmailService();

        // Armenian ("Ա" = U+0531) repeated to the full 1000 chars RejectListingRequest.Note allows.
        // Every character here is non-ASCII, so the default JavaScriptEncoder would escape each one
        // to \uXXXX (6 chars) — the worst case the review found breaks ModerationLogEntryConfiguration's
        // HasMaxLength(2000) at roughly 332 characters.
        var armenianNote = new string('Ա', 1000);

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId, email).RejectAsync(ListingId, "unsafeItem", armenianNote);

        Assert.True(result.IsSuccess);

        await using var verify = db.CreateContext();
        var log = await verify.ModerationLogEntries.SingleAsync(e => e.TargetId == ListingId);

        Assert.NotNull(log.DetailJson);
        Assert.True(log.DetailJson!.Length <= 2000,
            $"DetailJson was {log.DetailJson.Length} chars, exceeding the HasMaxLength(2000) column bound.");

        // Must still be well-formed JSON — truncation happens on the note before serialising, not
        // on the serialised string.
        using var parsed = JsonDocument.Parse(log.DetailJson);
        Assert.Equal("unsafeItem", parsed.RootElement.GetProperty("reasonCode").GetString());
        var loggedNote = parsed.RootElement.GetProperty("note").GetString();
        Assert.NotNull(loggedNote);
        Assert.True(loggedNote!.Length <= 300);

        // The full (untruncated) note is still on the listing itself — only the audit-log copy is bounded.
        var stored = await verify.Listings.FindAsync(ListingId);
        Assert.Equal(1000, stored!.RejectionNote!.Length);
    }

    // ---------- Wired placeholder: OwnerOpenReportCount ----------

    [Fact]
    public async Task GetDetail_OwnerOpenReportCount_Reflects_Open_Reports_Against_The_Owner()
    {
        using var db = new SqliteTestDatabase();
        await SeedAsync(db, ListingStatus.Approved);
        var reporterId = new Guid("d0000000-0000-0000-0000-000000000010");
        await db.SeedAsync(TestData.User(reporterId, "listing-detail-reporter@test.local"));
        await db.SeedAsync(
            TestData.Report(Guid.NewGuid(), ReportTargetType.User, OwnerId, reporterId, "noShow"),
            TestData.Report(Guid.NewGuid(), ReportTargetType.User, OwnerId, reporterId, "spam"),
            // Dismissed must not count towards OwnerOpenReportCount.
            TestData.Report(Guid.NewGuid(), ReportTargetType.User, OwnerId, reporterId, "other", ReportStatus.Dismissed),
            // A report against the LISTING (not the owner-as-user) must not count here either.
            TestData.Report(Guid.NewGuid(), ReportTargetType.Listing, ListingId, reporterId, "unsafeItem"));
        var email = new FakeEmailService();

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId, email).GetDetailAsync(ListingId);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value!.OwnerOpenReportCount);
    }
}
