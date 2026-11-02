using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RentalPlatform.Application.Services;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Infrastructure.Persistence;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Users;

// ADR-055 section 4 on the admin side: a suspension rotates TokenVersion in the same UPDATE and revokes
// after the commit; the idempotent and refused cases rotate nothing; reactivation only invalidates.
public sealed class AdminUsersSessionRevocationTests
{
    private static readonly Guid AdminId = new("a8000000-0000-0000-0000-000000000001");
    private static readonly Guid OtherAdminId = new("a8000000-0000-0000-0000-000000000002");
    private static readonly Guid UserId = new("a8000000-0000-0000-0000-000000000003");

    private static AdminUsersService CreateService(AppDbContext context, FakeSessionRevoker revoker) =>
        new(
            new FakeCurrentUserContext(AdminId),
            new AdminUsersStore(context),
            new ModerationLogStore(context, NullLogger<ModerationLogStore>.Instance),
            new FakeModerationNoteEmitter(),
            revoker);

    private static async Task SeedAsync(SqliteTestDatabase db, bool blocked = false) =>
        await db.SeedAsync(
            TestData.User(AdminId, "admin@revoke.local", role: UserRole.Admin, isIdConfirmed: true),
            TestData.User(OtherAdminId, "admin2@revoke.local", role: UserRole.Admin),
            TestData.User(UserId, "user@revoke.local", isBlocked: blocked));

    private static async Task<(int Version, bool Blocked)> StateAsync(SqliteTestDatabase db, Guid id)
    {
        await using var context = db.CreateContext();
        var user = await context.Users.AsNoTracking().SingleAsync(candidate => candidate.Id == id);
        return (user.TokenVersion, user.IsBlocked);
    }

    [Fact]
    public async Task Suspend_Blocks_And_Rotates_In_One_Step_Then_Revokes()
    {
        using var db = new SqliteTestDatabase();
        await SeedAsync(db);
        var revoker = new FakeSessionRevoker();
        await using var context = db.CreateContext();

        var result = await CreateService(context, revoker).SuspendAsync(UserId);

        Assert.True(result.IsSuccess);
        Assert.Equal((1, true), await StateAsync(db, UserId));
        Assert.Equal(new[] { UserId }, revoker.Revoked);
        Assert.Empty(revoker.Invalidated);
    }

    [Fact]
    public async Task Suspending_An_Already_Suspended_User_Is_A_No_Op_That_Rotates_And_Revokes_Nothing()
    {
        using var db = new SqliteTestDatabase();
        await SeedAsync(db, blocked: true);
        var revoker = new FakeSessionRevoker();
        await using var context = db.CreateContext();

        var result = await CreateService(context, revoker).SuspendAsync(UserId);

        Assert.True(result.IsSuccess);
        Assert.Equal((0, true), await StateAsync(db, UserId));
        Assert.Empty(revoker.Revoked);
    }

    [Fact]
    public async Task Suspending_An_Admin_Is_Refused_And_Rotates_Nothing()
    {
        using var db = new SqliteTestDatabase();
        await SeedAsync(db);
        var revoker = new FakeSessionRevoker();
        await using var context = db.CreateContext();

        var result = await CreateService(context, revoker).SuspendAsync(OtherAdminId);

        Assert.False(result.IsSuccess);
        Assert.Equal((0, false), await StateAsync(db, OtherAdminId));
        Assert.Empty(revoker.Revoked);
    }

    [Fact]
    public async Task Reactivate_Unblocks_Without_Rotating_And_Only_Invalidates_The_Cache()
    {
        using var db = new SqliteTestDatabase();
        await SeedAsync(db, blocked: true);
        var revoker = new FakeSessionRevoker();
        await using var context = db.CreateContext();

        var result = await CreateService(context, revoker).ReactivateAsync(UserId);

        Assert.True(result.IsSuccess);
        Assert.Equal((0, false), await StateAsync(db, UserId));
        Assert.Equal(new[] { UserId }, revoker.Invalidated);
        Assert.Empty(revoker.Revoked);
    }

    [Fact]
    public async Task Two_Blocks_Of_The_Same_Row_Rotate_Exactly_Once()
    {
        using var db = new SqliteTestDatabase();
        await SeedAsync(db);

        // Both stores read the user as unblocked; only the first conditional UPDATE matches the row.
        await using var first = db.CreateContext();
        await using var second = db.CreateContext();
        var firstUser = await new AdminUsersStore(first).FindUserByIdAsync(UserId);
        var secondUser = await new AdminUsersStore(second).FindUserByIdAsync(UserId);

        Assert.True(await new AdminUsersStore(first).TryBlockAsync(firstUser!));
        Assert.False(await new AdminUsersStore(second).TryBlockAsync(secondUser!));

        Assert.Equal((1, true), await StateAsync(db, UserId));
    }
}
