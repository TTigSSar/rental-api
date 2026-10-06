using System.Globalization;
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

    /// <summary>
    /// Whether the SQL Server these tests need is actually reachable, probed once per test run.
    /// </summary>
    /// <remarks>
    /// Read by <see cref="SqlServerFactAttribute"/>, which turns "no SQL Server on this machine"
    /// into a VISIBLE xUnit skip. The probe only opens a connection to master: it deliberately
    /// does not create, migrate or drop a database, so it is cheap enough to run at test-discovery
    /// time. Only a genuine connection failure counts as unavailable — a malformed
    /// <see cref="ConnectionStringEnvironmentVariable"/> still fails loudly rather than quietly
    /// skipping the whole SQL Server tier.
    /// </remarks>
    public static bool IsAvailable => LazyIsAvailable.Value;

    private static readonly Lazy<bool> LazyIsAvailable =
        new(ProbeAvailability, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Whether <see cref="ConnectionStringEnvironmentVariable"/> is set, i.e. whether this machine
    /// has explicitly DECLARED a SQL Server for these tests.
    /// </summary>
    /// <remarks>
    /// This is the load-bearing half of the skip contract, and it is not a simplification waiting
    /// to happen:
    ///
    ///   - Variable UNSET (a developer machine): no SQL Server means the SQL Server tier is
    ///     SKIPPED with a reason. The suite stays runnable without SQL Server.
    ///   - Variable SET (CI, per ADR-022): the environment has declared it HAS a SQL Server, so an
    ///     unreachable one is a broken environment, not an absent dependency. It must FAIL, never
    ///     skip. Without this, a SQL Server service container that failed to come up would make CI
    ///     pass with the migration tier silently unexecuted - the same false green this attribute
    ///     exists to remove from developer machines, relocated to where nobody is watching.
    ///
    /// <see cref="SqlServerFactAttribute"/> enforces it by declining to skip in the SET case: the
    /// test then runs and fails on the real connection error, which names the unreachable server.
    /// </remarks>
    public static bool IsExplicitlyConfigured =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionStringEnvironmentVariable));

    private static bool ProbeAvailability()
    {
        // Deliberately non-throwing: this runs while xUnit CONSTRUCTS SqlServerFactAttribute at
        // discovery time, and an exception there is not reliably reported as a failed run. The
        // "must fail when explicitly configured" half of the contract is therefore enforced by the
        // attribute (which declines to skip) and lands as an ordinary test failure.
        //
        // A short connect timeout keeps the probe from stalling the run on a machine with no
        // SQL Server, where the default 15s would be paid before the first skip is reported.
        var probeConnectionString =
            new SqlConnectionStringBuilder(MasterConnectionString) { ConnectTimeout = 5 }.ConnectionString;

        try
        {
            using var probe = new SqlConnection(probeConnectionString);
            probe.Open();
            return true;
        }
        catch (SqlException)
        {
            return false;
        }
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

    /// <summary>
    /// Inserts minimal Users rows with raw SQL, naming only the columns that have existed since
    /// InitialCreate.
    /// </summary>
    /// <remarks>
    /// Use this — not <see cref="SeedAsync"/> — in a test that seeds against an OLDER schema via
    /// <see cref="MigrateTo"/>. Seeding through the entity model writes every column the CURRENT
    /// model has, so the moment any migration adds a column to Users, every such test breaks with
    /// "Invalid column name" on a column that has nothing to do with what it is testing (which is
    /// exactly what AddUserHomePoint did). Raw SQL pins the insert to the old schema's shape.
    /// </remarks>
    public async Task SeedLegacyUsersAsync(params (Guid Id, string Email, int Role)[] users)
    {
        await using var context = CreateContext();
        foreach (var (id, email, role) in users)
        {
            await context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO [Users] ([Id], [Email], [PasswordHash], [FirstName], [LastName], [CreatedAt], [IsBlocked], [Role])
                VALUES ({0}, {1}, 'x', 'Test', 'User', SYSUTCDATETIME(), 0, {2});
                """,
                id, email, role);
        }
    }

    /// <summary>Inserts a minimal Categories row with raw SQL. Same reason as <see cref="SeedLegacyUsersAsync"/>.</summary>
    public async Task SeedLegacyCategoryAsync(Guid id, string name, string slug)
    {
        await using var context = CreateContext();
        await context.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO [Categories] ([Id], [Name], [Slug], [DisplayOrder], [IsVisible])
            VALUES ({0}, {1}, {2}, 0, 1);
            """,
            id, name, slug);
    }

    /// <summary>
    /// Inserts a minimal Listings row with raw SQL, naming only columns that exist at the schema
    /// state the caller migrated to. Same reason as <see cref="SeedLegacyUsersAsync"/>.
    /// </summary>
    /// <param name="latitude">Exact latitude, or null for a listing that never had a coordinate.</param>
    /// <param name="longitude">Exact longitude, or null for a listing that never had a coordinate.</param>
    /// <param name="status">
    /// Raw <c>Listings.Status</c> value. Left as a plain int rather than the ListingStatus enum on
    /// purpose: this method writes the OLD schema, and taking today's enum here would invite the
    /// same coupling the raw SQL exists to avoid.
    /// </param>
    /// <param name="updatedDaysAgo">
    /// How far in the past <c>UpdatedAt</c> is stamped. Defaults to a value well clear of "now" so a
    /// test can tell a preserved UpdatedAt from one a migration re-stamped with SYSUTCDATETIME().
    /// </param>
    /// <param name="city">
    /// Authored <c>City</c>. Parameterised because the pre-home-point schema let one owner's
    /// listings sit in different cities, and a migration that relocates a listing without moving
    /// its City can only be caught by seeding that shape.
    /// </param>
    /// <param name="country">Authored <c>Country</c>. Parameterised for the same reason as <paramref name="city"/>.</param>
    public async Task SeedLegacyListingAsync(
        Guid id,
        Guid ownerId,
        Guid categoryId,
        decimal? latitude,
        decimal? longitude,
        int createdDaysAgo,
        int status = 2,
        int updatedDaysAgo = 45,
        string city = "Yerevan",
        string country = "Armenia")
    {
        await using var context = CreateContext();

        // Coordinates are passed as STRINGS and converted in SQL. EF's raw-SQL parameter inference
        // maps a CLR decimal to decimal(18,2), which silently rounds 40.187400 to 40.19 — and a
        // migration test about preserving exact coordinates cannot start by losing four digits of
        // them. CONVERT pins the scale to the column's own decimal(9,6).
        //
        // A null coordinate is passed as the EMPTY STRING and turned back into NULL by NULLIF,
        // rather than as DBNull.Value: ExecuteSqlRawAsync infers a store type from each parameter's
        // CLR type and has no mapping for DBNull ("The current provider doesn't have a store type
        // mapping for properties of type 'DBNull'"). Every parameter here therefore stays nvarchar.
        await context.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO [Listings]
                ([Id], [OwnerId], [CategoryId], [Title], [Description], [PricePerDay], [PriceUnit],
                 [Currency], [Country], [City], [Latitude], [Longitude], [LocationKind], [Status],
                 [CreatedAt], [UpdatedAt])
            VALUES
                ({0}, {1}, {2}, 'Seeded listing', 'A long enough description to satisfy validation rules.',
                 2500, 1, 'AMD', {8}, {9},
                 CONVERT(decimal(9,6), NULLIF({3}, '')), CONVERT(decimal(9,6), NULLIF({4}, '')), 0, {6},
                 DATEADD(day, -{5}, SYSUTCDATETIME()), DATEADD(day, -{7}, SYSUTCDATETIME()));
            """,
            id,
            ownerId,
            categoryId,
            latitude?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            longitude?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            createdDaysAgo,
            status,
            updatedDaysAgo,
            country,
            city);
    }

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
