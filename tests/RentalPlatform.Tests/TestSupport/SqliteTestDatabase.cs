using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RentalPlatform.Infrastructure.Persistence;

namespace RentalPlatform.Tests.TestSupport;

// In-memory SQLite test database. SQLite (unlike the EF Core InMemory provider) is a
// real relational engine, so it honours transactions — required because
// BookingsStore.TryCreateBookingAsync opens a SERIALIZABLE transaction.
//
// The database lives for as long as the connection is open. Each test creates one
// instance (isolated DB). CreateContext() hands out fresh DbContext instances over
// the same connection so seeding and acting never share a change tracker — which
// keeps tests free of stale-entity surprises after ExecuteUpdate-style sweeps.
public sealed class SqliteTestDatabase : IDisposable
{
    private readonly SqliteConnection _connection;

    public SqliteTestDatabase()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        // EF Core's Sqlite provider translates string.Contains(...) to the builtin instr(),
        // which is byte-exact (case-sensitive). On SQL Server (production) the same LINQ call
        // translates to LIKE under the DB's default collation, which is case-insensitive
        // (SQL_Latin1_General_CP1_CI_AS) — e.g. ListingsQueryService's Search filter relies on
        // that CI collation rather than forcing ToLower() in the predicate. Overriding SQLite's
        // instr() here (permitted: SQLite explicitly allows redefining builtin functions) closes
        // that provider gap so tests exercise the same case-insensitive behavior production gets,
        // without changing the entity model, DbContext, or any migration.
        _connection.CreateFunction<string?, string?, long>(
            "instr",
            (haystack, needle) =>
            {
                if (haystack is null || needle is null)
                {
                    return 0;
                }

                var index = haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
                return index < 0 ? 0 : index + 1;
            });

        using var context = CreateContext();
        context.Database.EnsureCreated();
    }

    public AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options);

    // Persists the supplied entities through a dedicated context so the acting
    // context starts with an empty change tracker.
    public async Task SeedAsync(params object[] entities)
    {
        await using var context = CreateContext();
        context.AddRange(entities);
        await context.SaveChangesAsync();
    }

    public void Dispose() => _connection.Dispose();
}
