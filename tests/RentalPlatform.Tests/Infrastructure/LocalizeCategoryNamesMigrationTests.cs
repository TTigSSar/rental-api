using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using RentalPlatform.Infrastructure.DependencyInjection.DevelopmentSeed;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Infrastructure;

// ADR-062 / Trello #263. LocalizeCategoryNames adds NameHy/NameRu, backfills them in T-SQL, then
// alters both to NOT NULL. A row the backfill misses fails the production deploy inside the
// migration, and the only rows production really has beyond the 10 reference slugs are
// ADMIN-CREATED categories - which no seed-based or SQLite test ever produces. So this runs the
// real migration, on SQL Server, over exactly that shape.
public sealed class LocalizeCategoryNamesMigrationTests
{
    private const string MigrationBefore = "20261107121512_AddChatEmailPreferences";

    private static readonly Guid CustomCategoryId = new("c2000000-0000-0000-0000-000000000001");
    private const string CustomName = "Water Toys";
    private const string CustomSlug = "water-toys-admin-created";

    [SqlServerFact]
    public async Task Up_Backfills_Reference_Slugs_From_Seed_And_Falls_Back_To_English_For_Admin_Created_Rows()
    {
        using var db = new SqlServerTestDatabase();
        db.MigrateTo(MigrationBefore);

        var reference = DevelopmentSeedData.Categories.First(category => category.Slug == "puzzles");
        // The reference rows already exist (earlier migrations created them), so only the
        // admin-created one is inserted; the reference row is located by its stable slug.
        await db.SeedLegacyCategoryAsync(CustomCategoryId, CustomName, CustomSlug);

        // Must not throw: the NOT NULL alter fails if any row was left NULL.
        db.MigrateToLatest();

        await using var verify = db.CreateContext();
        var rows = await ReadNamesAsync(verify);
        var referenceId = rows.Single(row => row.Value.Slug == reference.Slug).Key;

        Assert.Equal((reference.Slug, reference.Name, reference.NameHy, reference.NameRu), rows[referenceId]);
        Assert.NotEqual(reference.Name, reference.NameHy); // the assertion above is not a same-value no-op

        Assert.Equal((CustomSlug, CustomName, CustomName, CustomName), rows[CustomCategoryId]);

        // Both columns really are NOT NULL now.
        await using var connection = new SqlConnection(verify.Database.GetConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('Categories') " +
            "AND name IN ('NameHy','NameRu') AND is_nullable = 0;";
        Assert.Equal(2, (int)(await command.ExecuteScalarAsync())!);
    }

    private static async Task<Dictionary<Guid, (string Slug, string Name, string NameHy, string NameRu)>> ReadNamesAsync(DbContext context)
    {
        var result = new Dictionary<Guid, (string, string, string, string)>();
        await using var connection = new SqlConnection(context.Database.GetConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT [Id], [Slug], [Name], [NameHy], [NameRu] FROM [Categories];";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result[reader.GetGuid(0)] = (reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4));
        }
        return result;
    }
}
