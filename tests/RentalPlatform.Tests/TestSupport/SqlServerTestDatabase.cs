using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using RentalPlatform.Infrastructure.Persistence;

namespace RentalPlatform.Tests.TestSupport;

// Real SQL Server test database, applied through the actual EF Core migrations (not
// EnsureCreated-from-model like SqliteTestDatabase). Reserved for the handful of tests that need
// genuine SQL Server semantics SQLite cannot provide: real concurrent writers (SQLite is
// effectively single-writer and serialises commands on a shared connection) and real
// Microsoft.Data.SqlClient.SqlException unique-violation numbers (2601/2627) — SQLite raises its
// own SqliteException instead, which ConversationsStore.GetOrCreateForModerationAsync's precise
// catch does not (and must not) match.
//
// Which SQL Server is used:
//   - By default, a local SQL Server reachable at "Server=.;Trusted_Connection=True" (the same
//     instance rental-api/CLAUDE.md's "Running locally" section assumes for RentalPlatformDbDev).
//     A developer machine needs no setup beyond that instance.
//   - If the environment variable RENTALPLATFORM_TEST_SQLSERVER_CONNECTION_STRING is set, its
//     value is used instead as the connection string. It must be a full ADO.NET SQL Server
//     connection string (any authentication mode); whatever database it names is ignored — this
//     class always connects to "master" for the create/drop and swaps in its own throwaway
//     database for everything else. Set by .github/workflows/backend-ci.yml, which has no local
//     SQL Server and cannot use Windows integrated auth at all (Linux runner), so it points these
//     tests at a SQL Server service container over SQL authentication.
//
// Each instance creates a uniquely-named throwaway database and drops it on Dispose.
public sealed class SqlServerTestDatabase : IDisposable
{
    /// <summary>
    /// Environment variable that overrides which SQL Server these tests run against. Unset on a
    /// developer machine (the local default applies); set in CI. See the class remarks.
    /// </summary>
    public const string ConnectionStringEnvironmentVariable =
        "RENTALPLATFORM_TEST_SQLSERVER_CONNECTION_STRING";

    private const string DefaultConnectionString =
        "Server=.;Trusted_Connection=True;TrustServerCertificate=True;";

    private static readonly string MasterConnectionString = BuildMasterConnectionString();

    private readonly string _databaseName = $"RentalPlatformDbTest_{Guid.NewGuid():N}";
    private readonly string _databaseConnectionString;

    public SqlServerTestDatabase()
    {
        // Derive the per-test database connection string by swapping the catalog on the parsed
        // connection string rather than interpolating a new one: interpolation would silently
        // drop every other keyword (credentials above all) the configured connection carries.
        _databaseConnectionString =
            new SqlConnectionStringBuilder(MasterConnectionString) { InitialCatalog = _databaseName }
                .ConnectionString;

        using (var master = new SqlConnection(MasterConnectionString))
        {
            master.Open();
            using var create = master.CreateCommand();
            create.CommandText = $"CREATE DATABASE [{_databaseName}];";
            create.ExecuteNonQuery();
        }

        // Applies every real migration, including the ones under test — the same code path
        // Program.cs's ApplyMigrationsAsync uses at startup.
        using var context = CreateContext();
        context.Database.Migrate();
    }

    private static string BuildMasterConnectionString()
    {
        var configured = Environment.GetEnvironmentVariable(ConnectionStringEnvironmentVariable);

        return new SqlConnectionStringBuilder(
            string.IsNullOrWhiteSpace(configured) ? DefaultConnectionString : configured)
        {
            InitialCatalog = "master"
        }.ConnectionString;
    }

    /// <summary>
    /// Migrates only up to (and including) <paramref name="targetMigration"/>, leaving later
    /// migrations — typically the one under test — unapplied. Lets a test seed data against the
    /// schema as it existed right before a migration, then apply that migration and assert on
    /// what it did.
    /// </summary>
    public void MigrateTo(string targetMigration)
    {
        using var context = CreateContext();
        context.GetInfrastructure().GetRequiredService<IMigrator>().Migrate(targetMigration);
    }

    /// <summary>Applies all remaining migrations (i.e. brings the database up to the latest).</summary>
    public void MigrateToLatest()
    {
        using var context = CreateContext();
        context.Database.Migrate();
    }

    public AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(_databaseConnectionString)
            .Options);

    public async Task SeedAsync(params object[] entities)
    {
        await using var context = CreateContext();
        context.AddRange(entities);
        await context.SaveChangesAsync();
    }

    public void Dispose()
    {
        using var master = new SqlConnection(MasterConnectionString);
        master.Open();
        using var drop = master.CreateCommand();
        // Kick out any lingering connections (e.g. pooled) before dropping.
        drop.CommandText =
            $"""
            ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
            DROP DATABASE [{_databaseName}];
            """;
        drop.ExecuteNonQuery();
    }
}
