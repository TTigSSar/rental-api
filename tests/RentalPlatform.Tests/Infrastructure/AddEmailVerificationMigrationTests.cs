using Microsoft.EntityFrameworkCore;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Infrastructure;

// The AddEmailVerification migration against a real SQL Server (same style and reasons as
// AddUserHomePointMigrationTests: the data step and the filtered unique index are T-SQL).
//
// Load-bearing assertions:
//  - Up grandfathers EVERY existing user as verified, with EmailConfirmedAt left NULL (that NULL is
//    what marks "grandfathered" rather than "proven"), so the hard gate locks nobody out on deploy;
//  - UserTokens carries the two unique indexes ADR-028 section 11 specifies, the second one filtered;
//  - Down drops the table and the column and does NOT un-verify anybody.
//
// [SqlServerFact]: skipped with a reason when no SQL Server is reachable on a developer machine.
public sealed class AddEmailVerificationMigrationTests
{
    private const string MigrationBefore = "20260927200321_AddUserHomePoint";

    private static readonly Guid FirstUserId = new("e3000000-0000-0000-0000-000000000001");
    private static readonly Guid SecondUserId = new("e3000000-0000-0000-0000-000000000002");
    private static readonly Guid AdminUserId = new("e3000000-0000-0000-0000-000000000003");

    private static async Task<int> ScalarAsync(SqlServerTestDatabase db, string sql)
    {
        await using var context = db.CreateContext();
        return (await context.Database.SqlQueryRaw<int>(sql).ToListAsync()).Single();
    }

    private static async Task SeedLegacyUsersAsync(SqlServerTestDatabase db)
    {
        // Raw SQL against the OLD schema (see SeedLegacyUsersAsync): none of these rows know about
        // EmailConfirmedAt, and none was ever confirmed (IsEmailConfirmed was never written).
        await db.SeedLegacyUsersAsync(
            (FirstUserId, "legacy.one@test.local", 0),
            (SecondUserId, "legacy.two@test.local", 0),
            (AdminUserId, "legacy.admin@test.local", 1));
    }

    [SqlServerFact]
    public async Task Up_Grandfathers_Every_Existing_User_As_Verified_Without_An_EmailConfirmedAt()
    {
        using var db = new SqlServerTestDatabase();
        db.MigrateTo(MigrationBefore);
        await SeedLegacyUsersAsync(db);
        Assert.Equal(3, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM [Users] WHERE [IsEmailConfirmed] = 0"));

        db.MigrateToLatest();

        Assert.Equal(0, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM [Users] WHERE [IsEmailConfirmed] = 0"));
        Assert.Equal(3, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM [Users] WHERE [IsEmailConfirmed] = 1 AND [EmailConfirmedAt] IS NULL"));
    }

    [SqlServerFact]
    public async Task Up_Creates_UserTokens_With_The_Two_Unique_Indexes_The_Second_One_Filtered()
    {
        using var db = new SqlServerTestDatabase();
        db.MigrateToLatest();

        Assert.Equal(1, await ScalarAsync(db,
            "SELECT COUNT(*) AS [Value] FROM sys.indexes WHERE object_id = OBJECT_ID('UserTokens') " +
            "AND name = 'IX_UserTokens_TokenHash' AND is_unique = 1 AND has_filter = 0"));
        Assert.Equal(1, await ScalarAsync(db,
            "SELECT COUNT(*) AS [Value] FROM sys.indexes WHERE object_id = OBJECT_ID('UserTokens') " +
            "AND name = 'IX_UserTokens_UserId_Purpose' AND is_unique = 1 AND has_filter = 1 " +
            "AND filter_definition = '([ConsumedAt] IS NULL)'"));
        Assert.Equal(1, await ScalarAsync(db,
            "SELECT COUNT(*) AS [Value] FROM sys.columns WHERE object_id = OBJECT_ID('UserTokens') " +
            "AND name = 'TokenHash' AND max_length = 32 AND system_type_id = TYPE_ID('binary')"));
    }

    [SqlServerFact]
    public async Task The_Filtered_Index_Allows_Many_Revoked_Tokens_But_Only_One_Active_One()
    {
        using var db = new SqlServerTestDatabase();
        db.MigrateToLatest();
        await db.SeedAsync(TestData.User(FirstUserId, "tokens@test.local", isEmailConfirmed: false));

        await using var context = db.CreateContext();
        await context.Database.ExecuteSqlRawAsync(
            "INSERT INTO [UserTokens] ([Id],[UserId],[Purpose],[TokenHash],[ExpiresAt],[ConsumedAt],[CreatedAt]) VALUES " +
            "(NEWID(), {0}, 1, 0x01, SYSUTCDATETIME(), SYSUTCDATETIME(), SYSUTCDATETIME())," +
            "(NEWID(), {0}, 1, 0x02, SYSUTCDATETIME(), SYSUTCDATETIME(), SYSUTCDATETIME())," +
            "(NEWID(), {0}, 1, 0x03, SYSUTCDATETIME(), NULL, SYSUTCDATETIME())",
            FirstUserId);

        var second = await Assert.ThrowsAnyAsync<Exception>(() => context.Database.ExecuteSqlRawAsync(
            "INSERT INTO [UserTokens] ([Id],[UserId],[Purpose],[TokenHash],[ExpiresAt],[ConsumedAt],[CreatedAt]) VALUES " +
            "(NEWID(), {0}, 1, 0x04, SYSUTCDATETIME(), NULL, SYSUTCDATETIME())",
            FirstUserId));
        Assert.Contains("UserTokens", second.ToString(), StringComparison.Ordinal);
    }

    [SqlServerFact]
    public async Task Down_Drops_The_Table_And_Column_And_Does_Not_Unverify_Anyone()
    {
        using var db = new SqlServerTestDatabase();
        db.MigrateTo(MigrationBefore);
        await SeedLegacyUsersAsync(db);
        db.MigrateToLatest();

        db.MigrateTo(MigrationBefore);

        Assert.Equal(0, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM sys.tables WHERE name = 'UserTokens'"));
        Assert.Equal(0, await ScalarAsync(db,
            "SELECT COUNT(*) AS [Value] FROM sys.columns WHERE object_id = OBJECT_ID('Users') AND name = 'EmailConfirmedAt'"));
        Assert.Equal(3, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM [Users] WHERE [IsEmailConfirmed] = 1"));
    }
}
