using Microsoft.EntityFrameworkCore;
using RentalPlatform.Application.DTOs;
using RentalPlatform.Application.Services;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Infrastructure.Persistence;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Reports;

// Admin console Phase 4: user-facing report submission (POST /api/reports). Covers the reason
// validity check, target-type parsing, target-existence checks for all three target kinds, the
// self-report guard, and — the anti-abuse guard that matters most — the duplicate-open-report
// guard.
public sealed class ReportsServiceTests
{
    private static readonly Guid ReporterId = new("e1000000-0000-0000-0000-000000000001");
    private static readonly Guid OwnerId = new("e1000000-0000-0000-0000-000000000002");
    private static readonly Guid OtherUserId = new("e1000000-0000-0000-0000-000000000003");
    private static readonly Guid CategoryId = new("e1000000-0000-0000-0000-000000000004");
    private static readonly Guid ListingId = new("e1000000-0000-0000-0000-000000000005");
    private static readonly Guid BookingId = new("e1000000-0000-0000-0000-000000000006");
    private static readonly Guid ConversationId = new("e1000000-0000-0000-0000-000000000007");

    private static ReportsService CreateService(AppDbContext context, Guid? currentUserId) =>
        new(new FakeCurrentUserContext(currentUserId), new ReportsStore(context));

    private static async Task SeedBaseAsync(SqliteTestDatabase db)
    {
        await db.SeedAsync(
            TestData.User(ReporterId, "reporter@test.local", firstName: "Rita", lastName: "Reporter"),
            TestData.User(OwnerId, "owner@test.local", firstName: "Olive", lastName: "Owner"),
            TestData.User(OtherUserId, "other@test.local", firstName: "Otto", lastName: "Other"),
            TestData.Category(CategoryId));
        await db.SeedAsync(TestData.Listing(ListingId, OwnerId, CategoryId, ListingStatus.Approved));
    }

    private static async Task SeedConversationAsync(SqliteTestDatabase db)
    {
        await db.SeedAsync(TestData.Booking(
            BookingId, ListingId, OtherUserId, TestData.Today, TestData.Today.AddDays(2), BookingStatus.Completed));
        await db.SeedAsync(TestData.Conversation(ConversationId, BookingId, OwnerId, OtherUserId));
    }

    private static CreateReportRequest ListingRequest(string reasonCode = "unsafeItem", string? detail = null) => new()
    {
        TargetType = "listing",
        TargetId = ListingId,
        ReasonCode = reasonCode,
        Detail = detail
    };

    // ---------- Success paths: severity/label per target type ----------

    [Fact]
    public async Task Create_Listing_Report_Succeeds_With_Derived_Severity_And_Listing_Title_As_Label()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaseAsync(db);

        await using var context = db.CreateContext();
        var result = await CreateService(context, ReporterId).CreateAsync(ListingRequest("unsafeItem"));

        Assert.True(result.IsSuccess);
        Assert.Equal(ReportTargetType.Listing, result.Value!.TargetType);
        Assert.Equal(ReportSeverity.High, result.Value.Severity); // unsafeItem => High
        Assert.Equal(ReportStatus.Open, result.Value.Status);
        Assert.Equal("LEGO Duplo Starter Set", result.Value.TargetLabel); // TestData.Listing's fixed title

        await using var verify = db.CreateContext();
        var stored = await verify.Reports.SingleAsync(r => r.Id == result.Value.Id);
        Assert.Equal(ReporterId, stored.ReporterUserId);
        Assert.Equal("unsafeItem", stored.ReasonCode);
    }

    [Fact]
    public async Task Create_User_Report_Succeeds_With_Full_Name_As_Label()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaseAsync(db);

        await using var context = db.CreateContext();
        var result = await CreateService(context, ReporterId).CreateAsync(new CreateReportRequest
        {
            TargetType = "user",
            TargetId = OtherUserId,
            ReasonCode = "rudeMessages"
        });

        Assert.True(result.IsSuccess);
        Assert.Equal(ReportTargetType.User, result.Value!.TargetType);
        Assert.Equal(ReportSeverity.Low, result.Value.Severity);
        Assert.Equal("Otto Other", result.Value.TargetLabel);
    }

    [Fact]
    public async Task Create_Message_Report_Succeeds_With_Chat_With_Counterpart_As_Label()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaseAsync(db);
        await SeedConversationAsync(db);

        // Reporter here is the conversation's Renter (OtherUserId is the renter side in
        // SeedConversationAsync); reuse a fresh reporter id seated as the renter instead so the
        // "reporter" semantics line up with the assertion below.
        await using var context = db.CreateContext();
        var result = await CreateService(context, OtherUserId).CreateAsync(new CreateReportRequest
        {
            TargetType = "message",
            TargetId = ConversationId,
            ReasonCode = "offPlatformPayment"
        });

        Assert.True(result.IsSuccess);
        Assert.Equal(ReportTargetType.Message, result.Value!.TargetType);
        Assert.Equal("Chat with Olive Owner", result.Value.TargetLabel); // reporter is Renter => counterpart is Owner
    }

    // ---------- Reason validity ----------

    [Fact]
    public async Task Create_Fails_When_Reason_Code_Unknown()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaseAsync(db);

        await using var context = db.CreateContext();
        var result = await CreateService(context, ReporterId).CreateAsync(ListingRequest("bogus"));

        Assert.False(result.IsSuccess);
        Assert.Equal("report.invalid_reason", result.Error!.Code);
    }

    // ---------- Target type parsing ----------

    [Fact]
    public async Task Create_Fails_When_Target_Type_Unrecognized()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaseAsync(db);

        await using var context = db.CreateContext();
        var result = await CreateService(context, ReporterId).CreateAsync(new CreateReportRequest
        {
            TargetType = "booking",
            TargetId = ListingId,
            ReasonCode = "other"
        });

        Assert.False(result.IsSuccess);
        Assert.Equal("report.invalid_target_type", result.Error!.Code);
    }

    // ---------- Target existence ----------

    [Fact]
    public async Task Create_Fails_When_Listing_Target_Not_Found()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaseAsync(db);

        await using var context = db.CreateContext();
        var result = await CreateService(context, ReporterId).CreateAsync(new CreateReportRequest
        {
            TargetType = "listing",
            TargetId = Guid.NewGuid(),
            ReasonCode = "spam"
        });

        Assert.False(result.IsSuccess);
        Assert.Equal("report.target_not_found", result.Error!.Code);
    }

    [Fact]
    public async Task Create_Fails_When_User_Target_Not_Found()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaseAsync(db);

        await using var context = db.CreateContext();
        var result = await CreateService(context, ReporterId).CreateAsync(new CreateReportRequest
        {
            TargetType = "user",
            TargetId = Guid.NewGuid(),
            ReasonCode = "spam"
        });

        Assert.False(result.IsSuccess);
        Assert.Equal("report.target_not_found", result.Error!.Code);
    }

    [Fact]
    public async Task Create_Fails_When_Message_Target_Not_Found()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaseAsync(db);

        await using var context = db.CreateContext();
        var result = await CreateService(context, ReporterId).CreateAsync(new CreateReportRequest
        {
            TargetType = "message",
            TargetId = Guid.NewGuid(),
            ReasonCode = "spam"
        });

        Assert.False(result.IsSuccess);
        Assert.Equal("report.target_not_found", result.Error!.Code);
    }

    // ---------- Self-report guard ----------

    [Fact]
    public async Task Create_Fails_When_Reporting_Own_Listing()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaseAsync(db);

        await using var context = db.CreateContext();
        var result = await CreateService(context, OwnerId).CreateAsync(ListingRequest());

        Assert.False(result.IsSuccess);
        Assert.Equal("report.cannot_report_own", result.Error!.Code);
    }

    [Fact]
    public async Task Create_Fails_When_Reporting_Self_As_User()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaseAsync(db);

        await using var context = db.CreateContext();
        var result = await CreateService(context, ReporterId).CreateAsync(new CreateReportRequest
        {
            TargetType = "user",
            TargetId = ReporterId,
            ReasonCode = "spam"
        });

        Assert.False(result.IsSuccess);
        Assert.Equal("report.cannot_report_own", result.Error!.Code);
    }

    // ---------- Duplicate-open-report guard (the key anti-abuse assertion) ----------

    [Fact]
    public async Task Create_Fails_When_Reporter_Already_Has_An_Open_Report_Against_The_Same_Target()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaseAsync(db);

        await using (var context = db.CreateContext())
        {
            var first = await CreateService(context, ReporterId).CreateAsync(ListingRequest("unsafeItem"));
            Assert.True(first.IsSuccess);
        }

        await using var second = db.CreateContext();
        // Even a different reason code against the same target is blocked while the first is Open.
        var result = await CreateService(second, ReporterId).CreateAsync(ListingRequest("misleadingPhotos"));

        Assert.False(result.IsSuccess);
        Assert.Equal("report.already_reported", result.Error!.Code);

        await using var verify = db.CreateContext();
        Assert.Equal(1, await verify.Reports.CountAsync(r => r.ReporterUserId == ReporterId && r.TargetId == ListingId));
    }

    [Fact]
    public async Task Create_Fails_Duplicate_Guard_Is_Scoped_Per_Reporter_Not_Global()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaseAsync(db);
        var secondReporterId = new Guid("e1000000-0000-0000-0000-000000000008");
        await db.SeedAsync(TestData.User(secondReporterId, "second-reporter@test.local"));

        await using (var context = db.CreateContext())
        {
            var first = await CreateService(context, ReporterId).CreateAsync(ListingRequest());
            Assert.True(first.IsSuccess);
        }

        // A different reporter against the same target is unaffected by the first reporter's
        // open report.
        await using var second = db.CreateContext();
        var result = await CreateService(second, secondReporterId).CreateAsync(ListingRequest());

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task Create_Succeeds_Again_When_The_Reporters_Prior_Report_Was_Already_Resolved()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaseAsync(db);
        // A previously RESOLVED report against the same target must not block a fresh report —
        // only an Open one does.
        await db.SeedAsync(TestData.Report(
            Guid.NewGuid(), ReportTargetType.Listing, ListingId, ReporterId, "unsafeItem", ReportStatus.Resolved));

        await using var context = db.CreateContext();
        var result = await CreateService(context, ReporterId).CreateAsync(ListingRequest("misleadingPhotos"));

        Assert.True(result.IsSuccess);
    }

    // ---------- Auth ----------

    [Fact]
    public async Task Create_Fails_When_Unauthenticated()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaseAsync(db);

        await using var context = db.CreateContext();
        var result = await CreateService(context, currentUserId: null).CreateAsync(ListingRequest());

        Assert.False(result.IsSuccess);
        Assert.Equal("report.unauthenticated", result.Error!.Code);
    }

    [Fact]
    public async Task Create_Fails_When_Reporter_Is_Blocked()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaseAsync(db);
        var blockedId = new Guid("e1000000-0000-0000-0000-000000000009");
        await db.SeedAsync(TestData.User(blockedId, "blocked@test.local", isBlocked: true));

        await using var context = db.CreateContext();
        var result = await CreateService(context, blockedId).CreateAsync(ListingRequest());

        Assert.False(result.IsSuccess);
        Assert.Equal("report.user_blocked", result.Error!.Code);
    }
}
