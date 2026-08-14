using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RentalPlatform.Application.DTOs;
using RentalPlatform.Application.Services;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Infrastructure.Persistence;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Reports;

// Admin console Phase 4: the Reports & flags screen backend. Covers the fixed severity-then-age
// sort, search across all four fields, status filtering + counts, pagination bounds,
// resolve/dismiss/reopen round-trips and their idempotency, one moderation-log entry per triage
// action, and admin-role enforcement.
public sealed class AdminReportsServiceTests
{
    private static readonly Guid AdminId = new("e2000000-0000-0000-0000-000000000001");
    private static readonly Guid ReporterId = new("e2000000-0000-0000-0000-000000000002");
    private static readonly Guid CategoryId = new("e2000000-0000-0000-0000-000000000003");
    private static readonly Guid ListingId = new("e2000000-0000-0000-0000-000000000004");
    private static readonly Guid OwnerId = new("e2000000-0000-0000-0000-000000000005");

    private static AdminReportsService CreateService(AppDbContext context, Guid? currentUserId) =>
        new(new FakeCurrentUserContext(currentUserId), new ReportsStore(context), new ModerationLogStore(context, NullLogger<ModerationLogStore>.Instance));

    private static async Task SeedAdminAsync(SqliteTestDatabase db) =>
        await db.SeedAsync(TestData.User(AdminId, "admin@test.local", role: UserRole.Admin, isIdConfirmed: true));

    private static async Task SeedBaseAsync(SqliteTestDatabase db)
    {
        await SeedAdminAsync(db);
        await db.SeedAsync(
            TestData.User(ReporterId, "reporter@test.local", firstName: "Rita", lastName: "Reporter"),
            TestData.User(OwnerId, "owner@test.local", firstName: "Olive", lastName: "Owner"),
            TestData.Category(CategoryId));
        await db.SeedAsync(TestData.Listing(ListingId, OwnerId, CategoryId, ListingStatus.Approved));
    }

    // ---------- Fixed sort: severity descending, then oldest first ----------

    [Fact]
    public async Task GetQueue_Sorts_By_Severity_Descending_Then_Oldest_First()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaseAsync(db);
        var now = DateTime.UtcNow;

        var lowNew = Guid.NewGuid();
        var highOld = Guid.NewGuid();
        var highNew = Guid.NewGuid();
        var mediumMid = Guid.NewGuid();
        await db.SeedAsync(
            TestData.Report(lowNew, ReportTargetType.Listing, ListingId, ReporterId, "other", createdAt: now.AddHours(-1)),
            TestData.Report(highOld, ReportTargetType.Listing, ListingId, ReporterId, "unsafeItem", createdAt: now.AddDays(-2)),
            TestData.Report(highNew, ReportTargetType.Listing, ListingId, ReporterId, "noShow", createdAt: now.AddHours(-2)),
            TestData.Report(mediumMid, ReportTargetType.Listing, ListingId, ReporterId, "spam", createdAt: now.AddDays(-1)));

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).GetQueueAsync(new AdminReportQueueFilter { Status = "all" });

        Assert.True(result.IsSuccess);
        var ids = result.Value!.Items.Select(i => i.Id).ToList();
        // A High report waiting two days (highOld) must outrank a High report from two hours ago
        // (highNew) — severity first, then oldest first within the same severity — and both must
        // outrank Medium/Low regardless of age.
        Assert.Equal([highOld, highNew, mediumMid, lowNew], ids);
    }

    // ---------- Search across all four fields ----------

    [Fact]
    public async Task GetQueue_Search_Matches_TargetLabel()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaseAsync(db);
        var id = Guid.NewGuid();
        await db.SeedAsync(TestData.Report(
            id, ReportTargetType.Listing, ListingId, ReporterId, "spam", targetLabel: "Very Unique Toy Title"));

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).GetQueueAsync(
            new AdminReportQueueFilter { Status = "all", Search = "Unique Toy" });

        Assert.Contains(id, result.Value!.Items.Select(i => i.Id));
    }

    [Fact]
    public async Task GetQueue_Search_Matches_Reporter_FirstName_LastName_FullName_And_Email()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaseAsync(db);
        var id = Guid.NewGuid();
        await db.SeedAsync(TestData.Report(id, ReportTargetType.Listing, ListingId, ReporterId, "spam"));

        await using var byFirst = db.CreateContext();
        Assert.Contains(id, (await CreateService(byFirst, AdminId).GetQueueAsync(
            new AdminReportQueueFilter { Status = "all", Search = "Rita" })).Value!.Items.Select(i => i.Id));

        await using var byLast = db.CreateContext();
        Assert.Contains(id, (await CreateService(byLast, AdminId).GetQueueAsync(
            new AdminReportQueueFilter { Status = "all", Search = "Reporter" })).Value!.Items.Select(i => i.Id));

        await using var byFull = db.CreateContext();
        Assert.Contains(id, (await CreateService(byFull, AdminId).GetQueueAsync(
            new AdminReportQueueFilter { Status = "all", Search = "Rita Reporter" })).Value!.Items.Select(i => i.Id));

        await using var byEmail = db.CreateContext();
        Assert.Contains(id, (await CreateService(byEmail, AdminId).GetQueueAsync(
            new AdminReportQueueFilter { Status = "all", Search = "reporter@test.local" })).Value!.Items.Select(i => i.Id));
    }

    [Fact]
    public async Task GetQueue_Search_Matches_Reason_Label_Not_Just_Raw_Code()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaseAsync(db);
        var id = Guid.NewGuid();
        // "noShow" label is "No-show at pickup" — search the label text, not the code.
        await db.SeedAsync(TestData.Report(id, ReportTargetType.Listing, ListingId, ReporterId, "noShow"));

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).GetQueueAsync(
            new AdminReportQueueFilter { Status = "all", Search = "pickup" });

        Assert.Contains(id, result.Value!.Items.Select(i => i.Id));
    }

    // ---------- Status filtering + counts ----------

    [Fact]
    public async Task GetQueue_Default_Status_Is_Open()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaseAsync(db);
        var openId = Guid.NewGuid();
        var resolvedId = Guid.NewGuid();
        await db.SeedAsync(
            TestData.Report(openId, ReportTargetType.Listing, ListingId, ReporterId, "spam", ReportStatus.Open),
            TestData.Report(resolvedId, ReportTargetType.Listing, ListingId, ReporterId, "spam", ReportStatus.Resolved));

        await using var context = db.CreateContext();
        // No Status supplied at all.
        var result = await CreateService(context, AdminId).GetQueueAsync(new AdminReportQueueFilter());

        var ids = result.Value!.Items.Select(i => i.Id).ToList();
        Assert.Contains(openId, ids);
        Assert.DoesNotContain(resolvedId, ids);
    }

    [Fact]
    public async Task GetQueue_Status_All_Returns_Every_Status()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaseAsync(db);
        var openId = Guid.NewGuid();
        var resolvedId = Guid.NewGuid();
        var dismissedId = Guid.NewGuid();
        await db.SeedAsync(
            TestData.Report(openId, ReportTargetType.Listing, ListingId, ReporterId, "spam", ReportStatus.Open),
            TestData.Report(resolvedId, ReportTargetType.Listing, ListingId, ReporterId, "spam", ReportStatus.Resolved),
            TestData.Report(dismissedId, ReportTargetType.Listing, ListingId, ReporterId, "spam", ReportStatus.Dismissed));

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).GetQueueAsync(new AdminReportQueueFilter { Status = "all" });

        var ids = result.Value!.Items.Select(i => i.Id).ToList();
        Assert.Contains(openId, ids);
        Assert.Contains(resolvedId, ids);
        Assert.Contains(dismissedId, ids);
    }

    [Fact]
    public async Task GetQueue_Counts_Are_Search_Filtered_Not_Status_Filtered()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaseAsync(db);
        await db.SeedAsync(
            TestData.Report(Guid.NewGuid(), ReportTargetType.Listing, ListingId, ReporterId, "spam", ReportStatus.Open, targetLabel: "Match Alpha"),
            TestData.Report(Guid.NewGuid(), ReportTargetType.Listing, ListingId, ReporterId, "spam", ReportStatus.Resolved, targetLabel: "Match Beta"),
            TestData.Report(Guid.NewGuid(), ReportTargetType.Listing, ListingId, ReporterId, "spam", ReportStatus.Dismissed, targetLabel: "Match Gamma"),
            // Not matched by "Match" search below.
            TestData.Report(Guid.NewGuid(), ReportTargetType.Listing, ListingId, ReporterId, "spam", ReportStatus.Open, targetLabel: "Unrelated"));

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).GetQueueAsync(
            new AdminReportQueueFilter { Status = "resolved", Search = "Match" });

        Assert.True(result.IsSuccess);
        // Status=resolved narrows Items to 1, but Counts reflect the search-filtered set only.
        Assert.Single(result.Value!.Items);
        Assert.Equal(1, result.Value.Counts.Open);
        Assert.Equal(1, result.Value.Counts.Resolved);
        Assert.Equal(1, result.Value.Counts.Dismissed);
        Assert.Equal(3, result.Value.Counts.All);
    }

    // ---------- Target filter (targetType/targetId) ----------

    [Fact]
    public async Task GetQueue_TargetType_And_TargetId_Filter_To_Exact_Target()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaseAsync(db);
        var otherListingId = Guid.NewGuid();
        var matchId = Guid.NewGuid();
        var otherListingReportId = Guid.NewGuid();
        var otherTypeReportId = Guid.NewGuid();
        await db.SeedAsync(
            TestData.Report(matchId, ReportTargetType.Listing, ListingId, ReporterId, "spam"),
            TestData.Report(otherListingReportId, ReportTargetType.Listing, otherListingId, ReporterId, "spam"),
            TestData.Report(otherTypeReportId, ReportTargetType.User, ListingId, ReporterId, "spam"));

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).GetQueueAsync(
            new AdminReportQueueFilter { Status = "all", TargetType = "Listing", TargetId = ListingId });

        Assert.True(result.IsSuccess);
        var ids = result.Value!.Items.Select(i => i.Id).ToList();
        Assert.Equal([matchId], ids);
    }

    [Fact]
    public async Task GetQueue_TargetType_Alone_Filters_To_All_Reports_Of_That_Type()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaseAsync(db);
        var listingReportId1 = Guid.NewGuid();
        var listingReportId2 = Guid.NewGuid();
        var userReportId = Guid.NewGuid();
        await db.SeedAsync(
            TestData.Report(listingReportId1, ReportTargetType.Listing, ListingId, ReporterId, "spam"),
            TestData.Report(listingReportId2, ReportTargetType.Listing, Guid.NewGuid(), ReporterId, "spam"),
            TestData.Report(userReportId, ReportTargetType.User, OwnerId, ReporterId, "spam"));

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).GetQueueAsync(
            new AdminReportQueueFilter { Status = "all", TargetType = "listing" });

        Assert.True(result.IsSuccess);
        var ids = result.Value!.Items.Select(i => i.Id).ToList();
        Assert.Contains(listingReportId1, ids);
        Assert.Contains(listingReportId2, ids);
        Assert.DoesNotContain(userReportId, ids);
    }

    [Fact]
    public async Task GetQueue_TargetId_Without_TargetType_Is_Rejected()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaseAsync(db);

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).GetQueueAsync(
            new AdminReportQueueFilter { TargetId = ListingId });

        Assert.False(result.IsSuccess);
        Assert.Equal("admin.report_target_filter_incomplete", result.Error!.Code);
    }

    [Fact]
    public async Task GetQueue_Unparseable_TargetType_Is_Rejected()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaseAsync(db);

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).GetQueueAsync(
            new AdminReportQueueFilter { TargetType = "not-a-real-type" });

        Assert.False(result.IsSuccess);
        Assert.Equal("admin.report_invalid_target_filter", result.Error!.Code);
    }

    [Fact]
    public async Task GetQueue_Counts_Respect_The_Target_Filter()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaseAsync(db);
        var otherListingId = Guid.NewGuid();
        await db.SeedAsync(
            // Matching target: one of each status.
            TestData.Report(Guid.NewGuid(), ReportTargetType.Listing, ListingId, ReporterId, "spam", ReportStatus.Open),
            TestData.Report(Guid.NewGuid(), ReportTargetType.Listing, ListingId, ReporterId, "spam", ReportStatus.Resolved),
            TestData.Report(Guid.NewGuid(), ReportTargetType.Listing, ListingId, ReporterId, "spam", ReportStatus.Dismissed),
            // Non-matching target: must not leak into the counts.
            TestData.Report(Guid.NewGuid(), ReportTargetType.Listing, otherListingId, ReporterId, "spam", ReportStatus.Open),
            TestData.Report(Guid.NewGuid(), ReportTargetType.User, ListingId, ReporterId, "spam", ReportStatus.Open));

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).GetQueueAsync(
            new AdminReportQueueFilter { Status = "open", TargetType = "Listing", TargetId = ListingId });

        Assert.True(result.IsSuccess);
        // Items narrowed by Status=open too, but Counts must reflect only the target-filtered set.
        Assert.Single(result.Value!.Items);
        Assert.Equal(1, result.Value.Counts.Open);
        Assert.Equal(1, result.Value.Counts.Resolved);
        Assert.Equal(1, result.Value.Counts.Dismissed);
        Assert.Equal(3, result.Value.Counts.All);
    }

    // ---------- No-filter regression guard ----------

    [Fact]
    public async Task GetQueue_With_No_Target_Filter_Returns_Reports_Of_All_Target_Types()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaseAsync(db);
        var listingReportId = Guid.NewGuid();
        var userReportId = Guid.NewGuid();
        var messageReportId = Guid.NewGuid();
        await db.SeedAsync(
            TestData.Report(listingReportId, ReportTargetType.Listing, ListingId, ReporterId, "spam"),
            TestData.Report(userReportId, ReportTargetType.User, OwnerId, ReporterId, "spam"),
            TestData.Report(messageReportId, ReportTargetType.Message, Guid.NewGuid(), ReporterId, "spam"));

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).GetQueueAsync(
            new AdminReportQueueFilter { Status = "all" });

        Assert.True(result.IsSuccess);
        var ids = result.Value!.Items.Select(i => i.Id).ToList();
        Assert.Contains(listingReportId, ids);
        Assert.Contains(userReportId, ids);
        Assert.Contains(messageReportId, ids);
    }

    // ---------- Pagination bounds ----------

    [Fact]
    public async Task GetQueue_Page_Below_One_Defaults_To_One()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaseAsync(db);

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).GetQueueAsync(new AdminReportQueueFilter { Page = 0 });

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value!.Page);
    }

    [Fact]
    public async Task GetQueue_PageSize_Is_Clamped_To_Max()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaseAsync(db);

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).GetQueueAsync(new AdminReportQueueFilter { PageSize = 5000 });

        Assert.True(result.IsSuccess);
        Assert.Equal(100, result.Value!.PageSize);
    }

    [Fact]
    public async Task GetQueue_TotalPages_Computed_From_TotalCount_And_PageSize()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaseAsync(db);
        for (var i = 0; i < 5; i++)
        {
            await db.SeedAsync(TestData.Report(Guid.NewGuid(), ReportTargetType.Listing, ListingId, ReporterId, "spam"));
        }

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).GetQueueAsync(
            new AdminReportQueueFilter { Status = "all", PageSize = 2 });

        Assert.True(result.IsSuccess);
        Assert.Equal(5, result.Value!.TotalCount);
        Assert.Equal(3, result.Value.TotalPages);
        Assert.Equal(2, result.Value.Items.Count);
    }

    // ---------- GetById ----------

    [Fact]
    public async Task GetById_Returns_The_Row_With_Reporter_Fields()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaseAsync(db);
        var id = Guid.NewGuid();
        await db.SeedAsync(TestData.Report(id, ReportTargetType.Listing, ListingId, ReporterId, "unsafeItem"));

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).GetByIdAsync(id);

        Assert.True(result.IsSuccess);
        Assert.Equal("Rita", result.Value!.ReporterFirstName);
        Assert.Equal(ReportSeverity.High, result.Value.Severity);
    }

    [Fact]
    public async Task GetById_Unknown_Id_Is_ReportNotFound()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaseAsync(db);

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).GetByIdAsync(Guid.NewGuid());

        Assert.False(result.IsSuccess);
        Assert.Equal("admin.report_not_found", result.Error!.Code);
    }

    // ---------- Resolve: round trip, idempotency, moderation log ----------

    [Fact]
    public async Task Resolve_Sets_Status_And_Stamps_And_Logs_Moderation_Entry()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaseAsync(db);
        var id = Guid.NewGuid();
        await db.SeedAsync(TestData.Report(id, ReportTargetType.Listing, ListingId, ReporterId, "unsafeItem"));

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).ResolveAsync(id, "Fixed by owner.");

        Assert.True(result.IsSuccess);
        Assert.Equal(ReportStatus.Resolved, result.Value!.Status);
        Assert.Equal("Fixed by owner.", result.Value.ResolutionNote);
        Assert.NotNull(result.Value.ResolvedAt);

        await using var verify = db.CreateContext();
        var stored = await verify.Reports.SingleAsync(r => r.Id == id);
        Assert.Equal(AdminId, stored.ResolvedByUserId);
        var log = await verify.ModerationLogEntries.SingleAsync(e => e.TargetId == id);
        Assert.Equal(ModerationAction.ReportResolved, log.Action);
        Assert.Equal(ModerationTargetType.Report, log.TargetType);
        Assert.Equal(AdminId, log.ActorUserId);
    }

    [Fact]
    public async Task Resolve_Is_Idempotent_When_Already_Resolved()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaseAsync(db);
        var id = Guid.NewGuid();
        await db.SeedAsync(TestData.Report(
            id, ReportTargetType.Listing, ListingId, ReporterId, "unsafeItem", ReportStatus.Resolved,
            resolvedAt: DateTime.UtcNow.AddDays(-1), resolvedByUserId: AdminId, resolutionNote: "Original note."));

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).ResolveAsync(id, "A different note.");

        Assert.True(result.IsSuccess);
        // No-op: the original note/stamp must be untouched by the second call.
        Assert.Equal("Original note.", result.Value!.ResolutionNote);

        await using var verify = db.CreateContext();
        Assert.False(await verify.ModerationLogEntries.AnyAsync(e => e.TargetId == id));
    }

    // ---------- MUST-FIX 1: DetailJson must not overflow its 2000-char column for non-ASCII notes ----------

    [Fact]
    public async Task Resolve_With_A_Full_Length_Armenian_Note_Produces_A_Bounded_Valid_DetailJson()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaseAsync(db);
        var id = Guid.NewGuid();
        await db.SeedAsync(TestData.Report(id, ReportTargetType.Listing, ListingId, ReporterId, "unsafeItem"));

        // Armenian ("Ա" = U+0531) repeated to the full 1000 chars ReportActionRequest.Note allows.
        // Every character here is non-ASCII, so the default JavaScriptEncoder would escape each one
        // to \uXXXX (6 chars) — the worst case the review found breaks ModerationLogEntryConfiguration's
        // HasMaxLength(2000) at roughly 332 characters.
        var armenianNote = new string('Ա', 1000);

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).ResolveAsync(id, armenianNote);

        Assert.True(result.IsSuccess);
        Assert.Equal(1000, result.Value!.ResolutionNote!.Length); // full note is still on the report itself

        await using var verify = db.CreateContext();
        var log = await verify.ModerationLogEntries.SingleAsync(e => e.TargetId == id);

        Assert.NotNull(log.DetailJson);
        Assert.True(log.DetailJson!.Length <= 2000,
            $"DetailJson was {log.DetailJson.Length} chars, exceeding the HasMaxLength(2000) column bound.");

        // Must still be well-formed JSON — truncation happens on the note before serialising, not
        // on the serialised string.
        using var parsed = JsonDocument.Parse(log.DetailJson);
        var loggedNote = parsed.RootElement.GetProperty("note").GetString();
        Assert.NotNull(loggedNote);
        Assert.True(loggedNote!.Length <= 300);
    }

    // ---------- Dismiss: round trip, idempotency, moderation log ----------

    [Fact]
    public async Task Dismiss_Sets_Status_And_Stamps_And_Logs_Moderation_Entry()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaseAsync(db);
        var id = Guid.NewGuid();
        await db.SeedAsync(TestData.Report(id, ReportTargetType.Listing, ListingId, ReporterId, "spam"));

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).DismissAsync(id, "Not actionable.");

        Assert.True(result.IsSuccess);
        Assert.Equal(ReportStatus.Dismissed, result.Value!.Status);
        Assert.Equal("Not actionable.", result.Value.ResolutionNote);

        await using var verify = db.CreateContext();
        var log = await verify.ModerationLogEntries.SingleAsync(e => e.TargetId == id);
        Assert.Equal(ModerationAction.ReportDismissed, log.Action);
    }

    [Fact]
    public async Task Dismiss_Is_Idempotent_When_Already_Dismissed()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaseAsync(db);
        var id = Guid.NewGuid();
        await db.SeedAsync(TestData.Report(
            id, ReportTargetType.Listing, ListingId, ReporterId, "spam", ReportStatus.Dismissed,
            resolvedAt: DateTime.UtcNow.AddDays(-1), resolvedByUserId: AdminId));

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).DismissAsync(id, "New note.");

        Assert.True(result.IsSuccess);
        await using var verify = db.CreateContext();
        Assert.False(await verify.ModerationLogEntries.AnyAsync(e => e.TargetId == id));
    }

    // ---------- Reopen: clears stamps, idempotency, moderation log, round trip ----------

    [Fact]
    public async Task Reopen_Clears_Resolution_Stamps_And_Logs_Moderation_Entry()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaseAsync(db);
        var id = Guid.NewGuid();
        await db.SeedAsync(TestData.Report(
            id, ReportTargetType.Listing, ListingId, ReporterId, "spam", ReportStatus.Resolved,
            resolvedAt: DateTime.UtcNow.AddDays(-1), resolvedByUserId: AdminId, resolutionNote: "Some note."));

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).ReopenAsync(id);

        Assert.True(result.IsSuccess);
        Assert.Equal(ReportStatus.Open, result.Value!.Status);
        Assert.Null(result.Value.ResolvedAt);
        Assert.Null(result.Value.ResolutionNote);

        await using var verify = db.CreateContext();
        var stored = await verify.Reports.SingleAsync(r => r.Id == id);
        Assert.Null(stored.ResolvedByUserId);
        var log = await verify.ModerationLogEntries.SingleAsync(e => e.TargetId == id);
        Assert.Equal(ModerationAction.ReportReopened, log.Action);
    }

    [Fact]
    public async Task Reopen_Is_Idempotent_When_Already_Open()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaseAsync(db);
        var id = Guid.NewGuid();
        await db.SeedAsync(TestData.Report(id, ReportTargetType.Listing, ListingId, ReporterId, "spam", ReportStatus.Open));

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).ReopenAsync(id);

        Assert.True(result.IsSuccess);
        await using var verify = db.CreateContext();
        Assert.False(await verify.ModerationLogEntries.AnyAsync(e => e.TargetId == id));
    }

    [Fact]
    public async Task Resolve_Then_Reopen_Round_Trip()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaseAsync(db);
        var id = Guid.NewGuid();
        await db.SeedAsync(TestData.Report(id, ReportTargetType.Listing, ListingId, ReporterId, "spam"));

        await using (var context = db.CreateContext())
        {
            var resolveResult = await CreateService(context, AdminId).ResolveAsync(id, "Handled.");
            Assert.True(resolveResult.IsSuccess);
            Assert.Equal(ReportStatus.Resolved, resolveResult.Value!.Status);
        }

        await using (var context = db.CreateContext())
        {
            var reopenResult = await CreateService(context, AdminId).ReopenAsync(id);
            Assert.True(reopenResult.IsSuccess);
            Assert.Equal(ReportStatus.Open, reopenResult.Value!.Status);
        }

        await using var verify = db.CreateContext();
        Assert.Equal(2, await verify.ModerationLogEntries.CountAsync(e => e.TargetId == id));
    }

    [Fact]
    public async Task Resolve_Dismiss_Reopen_Unknown_Id_Is_ReportNotFound()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaseAsync(db);

        await using var context = db.CreateContext();
        var service = CreateService(context, AdminId);
        var unknownId = Guid.NewGuid();

        Assert.Equal("admin.report_not_found", (await service.ResolveAsync(unknownId, null)).Error!.Code);
        Assert.Equal("admin.report_not_found", (await service.DismissAsync(unknownId, null)).Error!.Code);
        Assert.Equal("admin.report_not_found", (await service.ReopenAsync(unknownId)).Error!.Code);
    }

    // ---------- TargetImageUrl resolution ----------

    [Fact]
    public async Task GetById_Resolves_TargetImageUrl_From_Listing_Primary_Image()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaseAsync(db);
        await db.SeedAsync(
            TestData.Image(Guid.NewGuid(), ListingId, isPrimary: false, sortOrder: 0),
            TestData.Image(Guid.NewGuid(), ListingId, isPrimary: true, sortOrder: 1));
        var id = Guid.NewGuid();
        await db.SeedAsync(TestData.Report(id, ReportTargetType.Listing, ListingId, ReporterId, "spam"));

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).GetByIdAsync(id);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value!.TargetImageUrl);
    }

    [Fact]
    public async Task GetById_TargetImageUrl_Is_Null_For_A_Message_Target()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaseAsync(db);
        var id = Guid.NewGuid();
        await db.SeedAsync(TestData.Report(id, ReportTargetType.Message, Guid.NewGuid(), ReporterId, "spam"));

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).GetByIdAsync(id);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value!.TargetImageUrl);
    }

    // ---------- Admin-role enforcement (defence in depth) ----------

    [Fact]
    public async Task GetQueue_By_Non_Admin_Is_Forbidden()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaseAsync(db);
        var nonAdminId = new Guid("e2000000-0000-0000-0000-000000000099");
        await db.SeedAsync(TestData.User(nonAdminId, "nonadmin@test.local"));

        await using var context = db.CreateContext();
        var result = await CreateService(context, nonAdminId).GetQueueAsync(new AdminReportQueueFilter());

        Assert.False(result.IsSuccess);
        Assert.Equal("admin.forbidden", result.Error!.Code);
    }

    [Fact]
    public async Task GetQueue_Unauthenticated_Is_Unauthenticated()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaseAsync(db);

        await using var context = db.CreateContext();
        var result = await CreateService(context, currentUserId: null).GetQueueAsync(new AdminReportQueueFilter());

        Assert.False(result.IsSuccess);
        Assert.Equal("admin.unauthenticated", result.Error!.Code);
    }
}
