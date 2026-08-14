using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RentalPlatform.Application.DTOs;
using RentalPlatform.Application.Services;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Infrastructure.Persistence;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Categories;

// Admin console Phase 2: the Categories screen backend. Covers admin role enforcement, the
// listing-count aggregate, duplicate-name/slug rejection, reorder (happy path + mismatch),
// delete (empty / reassign / refusal without a target), visibility toggle, and one moderation-log
// entry per mutation.
public sealed class AdminCategoriesServiceTests
{
    private static readonly Guid AdminId = new("e0000000-0000-0000-0000-000000000001");
    private static readonly Guid OwnerId = new("e0000000-0000-0000-0000-000000000002");

    private static async Task SeedAdminAndOwnerAsync(SqliteTestDatabase db)
    {
        await db.SeedAsync(
            TestData.User(AdminId, "admin@test.local", role: UserRole.Admin),
            TestData.User(OwnerId, "owner@test.local"));
    }

    private static AdminCategoriesService CreateService(AppDbContext context, Guid currentUserId) =>
        new(
            new FakeCurrentUserContext(currentUserId),
            new AdminCategoriesStore(context),
            new ModerationLogStore(context, NullLogger<ModerationLogStore>.Instance));

    // ---------- Listing-count correctness (Approved only) ----------

    [Fact]
    public async Task GetAll_ListingCount_Counts_Approved_Listings_Only()
    {
        using var db = new SqliteTestDatabase();
        await SeedAdminAndOwnerAsync(db);
        var categoryId = new Guid("e0000000-0000-0000-0000-000000000010");
        await db.SeedAsync(TestData.Category(categoryId, name: "Puzzles", slug: "puzzles-t1", displayOrder: 1));
        await db.SeedAsync(
            TestData.Listing(new Guid("e0000000-0000-0000-0000-000000000011"), OwnerId, categoryId, ListingStatus.Approved),
            TestData.Listing(new Guid("e0000000-0000-0000-0000-000000000012"), OwnerId, categoryId, ListingStatus.Approved),
            TestData.Listing(new Guid("e0000000-0000-0000-0000-000000000013"), OwnerId, categoryId, ListingStatus.PendingApproval),
            TestData.Listing(new Guid("e0000000-0000-0000-0000-000000000014"), OwnerId, categoryId, ListingStatus.Rejected));

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).GetAllAsync();

        Assert.True(result.IsSuccess);
        var row = Assert.Single(result.Value!.Items);
        Assert.Equal(2, row.ListingCount);
        Assert.Equal(1, result.Value.Summary.TotalCategories);
        Assert.Equal(2, result.Value.Summary.TotalListedToys);
        Assert.Equal(1, result.Value.Summary.VisibleCount);
    }

    [Fact]
    public async Task GetAll_By_Non_Admin_Is_Forbidden()
    {
        using var db = new SqliteTestDatabase();
        await SeedAdminAndOwnerAsync(db);

        await using var context = db.CreateContext();
        var result = await CreateService(context, OwnerId).GetAllAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal("admin.forbidden", result.Error!.Code);
    }

    // ---------- Create: duplicate name/slug rejection + moderation log ----------

    [Fact]
    public async Task Create_Rejects_Duplicate_Name_Case_Insensitive()
    {
        using var db = new SqliteTestDatabase();
        await SeedAdminAndOwnerAsync(db);
        await db.SeedAsync(TestData.Category(
            new Guid("e0000000-0000-0000-0000-000000000020"), name: "Board Games", slug: "board-games-t1", displayOrder: 1));

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).CreateAsync(
            new CreateCategoryRequest { Name = "board games" });

        Assert.False(result.IsSuccess);
        Assert.Equal("admin.category_name_taken", result.Error!.Code);
    }

    [Fact]
    public async Task Create_Appends_At_End_Of_Order_And_Logs_Moderation_Entry()
    {
        using var db = new SqliteTestDatabase();
        await SeedAdminAndOwnerAsync(db);
        await db.SeedAsync(TestData.Category(
            new Guid("e0000000-0000-0000-0000-000000000021"), name: "Puzzles", slug: "puzzles-t2", displayOrder: 5));

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).CreateAsync(
            new CreateCategoryRequest { Name = "Water Toys", IconName = "pi-droplet", ColorHex = "#AABBCC" });

        Assert.True(result.IsSuccess);
        Assert.Equal("water-toys", result.Value!.Slug);
        Assert.Equal(6, result.Value.DisplayOrder);
        Assert.True(result.Value.IsVisible);
        Assert.Equal(0, result.Value.ListingCount);

        await using var verify = db.CreateContext();
        var log = await verify.ModerationLogEntries.SingleAsync(e => e.TargetId == result.Value.Id);
        Assert.Equal(ModerationAction.CategoryCreated, log.Action);
        Assert.Equal(ModerationTargetType.Category, log.TargetType);
        Assert.Equal(AdminId, log.ActorUserId);
    }

    // ---------- Update: rename duplicate guard, slug stability, moderation log ----------

    [Fact]
    public async Task Update_Rename_Does_Not_Change_Slug()
    {
        using var db = new SqliteTestDatabase();
        await SeedAdminAndOwnerAsync(db);
        var categoryId = new Guid("e0000000-0000-0000-0000-000000000030");
        await db.SeedAsync(TestData.Category(categoryId, name: "Puzzles", slug: "puzzles-t3", displayOrder: 1));

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).UpdateAsync(
            categoryId, new UpdateCategoryRequest { Name = "Brain Puzzles" });

        Assert.True(result.IsSuccess);
        Assert.Equal("Brain Puzzles", result.Value!.Name);
        Assert.Equal("puzzles-t3", result.Value.Slug); // unchanged

        await using var verify = db.CreateContext();
        var log = await verify.ModerationLogEntries.SingleAsync(e => e.TargetId == categoryId);
        Assert.Equal(ModerationAction.CategoryRenamed, log.Action);
    }

    [Fact]
    public async Task Update_Rejects_Rename_To_An_Existing_Name()
    {
        using var db = new SqliteTestDatabase();
        await SeedAdminAndOwnerAsync(db);
        var categoryId = new Guid("e0000000-0000-0000-0000-000000000031");
        await db.SeedAsync(
            TestData.Category(categoryId, name: "Puzzles", slug: "puzzles-t4", displayOrder: 1),
            TestData.Category(new Guid("e0000000-0000-0000-0000-000000000032"), name: "Board Games", slug: "board-games-t4", displayOrder: 2));

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).UpdateAsync(
            categoryId, new UpdateCategoryRequest { Name = "Board Games" });

        Assert.False(result.IsSuccess);
        Assert.Equal("admin.category_name_taken", result.Error!.Code);
    }

    [Fact]
    public async Task Update_Invalid_Color_Is_Rejected()
    {
        using var db = new SqliteTestDatabase();
        await SeedAdminAndOwnerAsync(db);
        var categoryId = new Guid("e0000000-0000-0000-0000-000000000033");
        await db.SeedAsync(TestData.Category(categoryId, name: "Puzzles", slug: "puzzles-t5", displayOrder: 1));

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).UpdateAsync(
            categoryId, new UpdateCategoryRequest { ColorHex = "not-a-color" });

        Assert.False(result.IsSuccess);
        Assert.Equal("admin.category_invalid_color", result.Error!.Code);
    }

    // ---------- Visibility toggle ----------

    [Fact]
    public async Task UpdateVisibility_Toggles_And_Logs_Moderation_Entry()
    {
        using var db = new SqliteTestDatabase();
        await SeedAdminAndOwnerAsync(db);
        var categoryId = new Guid("e0000000-0000-0000-0000-000000000040");
        await db.SeedAsync(TestData.Category(categoryId, name: "Puzzles", slug: "puzzles-t6", displayOrder: 1, isVisible: true));

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).UpdateVisibilityAsync(categoryId, false);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value!.IsVisible);

        await using var verify = db.CreateContext();
        var stored = await verify.Categories.FindAsync(categoryId);
        Assert.False(stored!.IsVisible);
        var log = await verify.ModerationLogEntries.SingleAsync(e => e.TargetId == categoryId);
        Assert.Equal(ModerationAction.CategoryVisibilityChanged, log.Action);
    }

    [Fact]
    public async Task UpdateVisibility_NoOp_When_Already_At_Target_Writes_No_Log()
    {
        using var db = new SqliteTestDatabase();
        await SeedAdminAndOwnerAsync(db);
        var categoryId = new Guid("e0000000-0000-0000-0000-000000000041");
        await db.SeedAsync(TestData.Category(categoryId, name: "Puzzles", slug: "puzzles-t7", displayOrder: 1, isVisible: true));

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).UpdateVisibilityAsync(categoryId, true);

        Assert.True(result.IsSuccess);

        await using var verify = db.CreateContext();
        Assert.False(await verify.ModerationLogEntries.AnyAsync(e => e.TargetId == categoryId));
    }

    // ---------- Reorder: happy path + mismatch rejection ----------

    [Fact]
    public async Task Reorder_Assigns_DisplayOrder_By_Array_Position()
    {
        using var db = new SqliteTestDatabase();
        await SeedAdminAndOwnerAsync(db);
        var idA = new Guid("e0000000-0000-0000-0000-000000000050");
        var idB = new Guid("e0000000-0000-0000-0000-000000000051");
        var idC = new Guid("e0000000-0000-0000-0000-000000000052");
        await db.SeedAsync(
            TestData.Category(idA, name: "A", slug: "a-t1", displayOrder: 0),
            TestData.Category(idB, name: "B", slug: "b-t1", displayOrder: 1),
            TestData.Category(idC, name: "C", slug: "c-t1", displayOrder: 2));

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).ReorderAsync([idC, idA, idB]);

        Assert.True(result.IsSuccess);

        await using var verify = db.CreateContext();
        Assert.Equal(1, (await verify.Categories.FindAsync(idA))!.DisplayOrder);
        Assert.Equal(2, (await verify.Categories.FindAsync(idB))!.DisplayOrder);
        Assert.Equal(0, (await verify.Categories.FindAsync(idC))!.DisplayOrder);

        var log = await verify.ModerationLogEntries.SingleAsync(e => e.Action == ModerationAction.CategoryReordered);
        Assert.Equal(ModerationTargetType.Category, log.TargetType);
    }

    [Fact]
    public async Task Reorder_Rejects_Partial_Id_Set()
    {
        using var db = new SqliteTestDatabase();
        await SeedAdminAndOwnerAsync(db);
        var idA = new Guid("e0000000-0000-0000-0000-000000000053");
        var idB = new Guid("e0000000-0000-0000-0000-000000000054");
        await db.SeedAsync(
            TestData.Category(idA, name: "A", slug: "a-t2", displayOrder: 0),
            TestData.Category(idB, name: "B", slug: "b-t2", displayOrder: 1));

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).ReorderAsync([idA]); // missing idB

        Assert.False(result.IsSuccess);
        Assert.Equal("admin.category_order_mismatch", result.Error!.Code);

        await using var verify = db.CreateContext();
        Assert.Equal(0, (await verify.Categories.FindAsync(idA))!.DisplayOrder); // untouched
    }

    [Fact]
    public async Task Reorder_Rejects_Unknown_Id_In_Set()
    {
        using var db = new SqliteTestDatabase();
        await SeedAdminAndOwnerAsync(db);
        var idA = new Guid("e0000000-0000-0000-0000-000000000055");
        var unknown = new Guid("e0000000-0000-0000-0000-000000000056");
        await db.SeedAsync(TestData.Category(idA, name: "A", slug: "a-t3", displayOrder: 0));

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).ReorderAsync([idA, unknown]);

        Assert.False(result.IsSuccess);
        Assert.Equal("admin.category_order_mismatch", result.Error!.Code);
    }

    // ---------- Delete: empty category, refusal without target, reassign of every status ----------

    [Fact]
    public async Task Delete_Empty_Category_Succeeds_Without_ReassignTarget()
    {
        using var db = new SqliteTestDatabase();
        await SeedAdminAndOwnerAsync(db);
        var categoryId = new Guid("e0000000-0000-0000-0000-000000000060");
        await db.SeedAsync(TestData.Category(categoryId, name: "Empty", slug: "empty-t1", displayOrder: 1));

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).DeleteAsync(categoryId, reassignToCategoryId: null);

        Assert.True(result.IsSuccess);

        await using var verify = db.CreateContext();
        Assert.Null(await verify.Categories.FindAsync(categoryId));
        var log = await verify.ModerationLogEntries.SingleAsync(e => e.TargetId == categoryId);
        Assert.Equal(ModerationAction.CategoryDeleted, log.Action);
    }

    [Fact]
    public async Task Delete_NonEmpty_Category_Without_Target_Is_Refused()
    {
        using var db = new SqliteTestDatabase();
        await SeedAdminAndOwnerAsync(db);
        var categoryId = new Guid("e0000000-0000-0000-0000-000000000061");
        await db.SeedAsync(TestData.Category(categoryId, name: "Occupied", slug: "occupied-t1", displayOrder: 1));
        await db.SeedAsync(TestData.Listing(new Guid("e0000000-0000-0000-0000-000000000062"), OwnerId, categoryId));

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).DeleteAsync(categoryId, reassignToCategoryId: null);

        Assert.False(result.IsSuccess);
        Assert.Equal("admin.category_reassign_required", result.Error!.Code);

        await using var verify = db.CreateContext();
        Assert.NotNull(await verify.Categories.FindAsync(categoryId)); // not deleted
    }

    [Fact]
    public async Task Delete_With_Reassign_Moves_Listings_Of_Every_Status_Then_Deletes_Source()
    {
        using var db = new SqliteTestDatabase();
        await SeedAdminAndOwnerAsync(db);
        var sourceId = new Guid("e0000000-0000-0000-0000-000000000070");
        var targetId = new Guid("e0000000-0000-0000-0000-000000000071");
        await db.SeedAsync(
            TestData.Category(sourceId, name: "Source", slug: "source-t1", displayOrder: 1),
            TestData.Category(targetId, name: "Target", slug: "target-t1", displayOrder: 2));

        var approvedId = new Guid("e0000000-0000-0000-0000-000000000072");
        var pendingId = new Guid("e0000000-0000-0000-0000-000000000073");
        var rejectedId = new Guid("e0000000-0000-0000-0000-000000000074");
        var archivedId = new Guid("e0000000-0000-0000-0000-000000000075");
        var draftId = new Guid("e0000000-0000-0000-0000-000000000076");
        await db.SeedAsync(
            TestData.Listing(approvedId, OwnerId, sourceId, ListingStatus.Approved),
            TestData.Listing(pendingId, OwnerId, sourceId, ListingStatus.PendingApproval),
            TestData.Listing(rejectedId, OwnerId, sourceId, ListingStatus.Rejected),
            TestData.Listing(archivedId, OwnerId, sourceId, ListingStatus.Archived),
            TestData.Listing(draftId, OwnerId, sourceId, ListingStatus.Draft));

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).DeleteAsync(sourceId, targetId);

        Assert.True(result.IsSuccess);

        await using var verify = db.CreateContext();
        Assert.Null(await verify.Categories.FindAsync(sourceId));
        Assert.NotNull(await verify.Categories.FindAsync(targetId));

        var movedIds = new[] { approvedId, pendingId, rejectedId, archivedId, draftId };
        foreach (var id in movedIds)
        {
            var listing = await verify.Listings.FindAsync(id);
            Assert.Equal(targetId, listing!.CategoryId);
        }

        var log = await verify.ModerationLogEntries.SingleAsync(e => e.TargetId == sourceId);
        Assert.Equal(ModerationAction.CategoryDeleted, log.Action);
    }

    [Fact]
    public async Task Delete_With_Reassign_To_Self_Is_Rejected()
    {
        using var db = new SqliteTestDatabase();
        await SeedAdminAndOwnerAsync(db);
        var categoryId = new Guid("e0000000-0000-0000-0000-000000000080");
        await db.SeedAsync(TestData.Category(categoryId, name: "Self", slug: "self-t1", displayOrder: 1));
        await db.SeedAsync(TestData.Listing(new Guid("e0000000-0000-0000-0000-000000000081"), OwnerId, categoryId));

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).DeleteAsync(categoryId, categoryId);

        Assert.False(result.IsSuccess);
        Assert.Equal("admin.category_reassign_invalid", result.Error!.Code);
    }

    [Fact]
    public async Task Delete_With_Reassign_To_Missing_Target_Fails()
    {
        using var db = new SqliteTestDatabase();
        await SeedAdminAndOwnerAsync(db);
        var categoryId = new Guid("e0000000-0000-0000-0000-000000000082");
        await db.SeedAsync(TestData.Category(categoryId, name: "Occupied", slug: "occupied-t2", displayOrder: 1));
        await db.SeedAsync(TestData.Listing(new Guid("e0000000-0000-0000-0000-000000000083"), OwnerId, categoryId));

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).DeleteAsync(
            categoryId, new Guid("e0000000-0000-0000-0000-0000000000ff"));

        Assert.False(result.IsSuccess);
        Assert.Equal("admin.category_reassign_target_not_found", result.Error!.Code);
    }

    // ---------- admin.category_not_found is reserved for the ROUTE id (Part 1b) ----------

    [Fact]
    public async Task Update_Unknown_Route_Id_Returns_CategoryNotFound()
    {
        using var db = new SqliteTestDatabase();
        await SeedAdminAndOwnerAsync(db);

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).UpdateAsync(
            Guid.NewGuid(), new UpdateCategoryRequest { Name = "Whatever" });

        Assert.False(result.IsSuccess);
        Assert.Equal("admin.category_not_found", result.Error!.Code);
    }

    [Fact]
    public async Task UpdateVisibility_Unknown_Route_Id_Returns_CategoryNotFound()
    {
        using var db = new SqliteTestDatabase();
        await SeedAdminAndOwnerAsync(db);

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).UpdateVisibilityAsync(Guid.NewGuid(), false);

        Assert.False(result.IsSuccess);
        Assert.Equal("admin.category_not_found", result.Error!.Code);
    }

    [Fact]
    public async Task Delete_Unknown_Route_Id_Returns_CategoryNotFound()
    {
        using var db = new SqliteTestDatabase();
        await SeedAdminAndOwnerAsync(db);

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).DeleteAsync(Guid.NewGuid(), reassignToCategoryId: null);

        Assert.False(result.IsSuccess);
        Assert.Equal("admin.category_not_found", result.Error!.Code);
    }
}
