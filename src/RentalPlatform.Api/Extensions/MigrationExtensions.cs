using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.SqlServer.Storage.Internal;
using RentalPlatform.Infrastructure.Persistence;

namespace RentalPlatform.Api.Extensions;

public static class MigrationExtensions
{
    internal const int MaxMigrationAttempts = 10;
    internal static readonly TimeSpan MigrationRetryDelay = TimeSpan.FromSeconds(5);

    // Applies pending EF Core migrations on startup. Runs in all environments so the
    // schema is current before any traffic is served and before the dev seed runs.
    //
    // M-005: after `docker compose down` + `up` (volume kept) the db healthcheck (SELECT 1 on
    // master) passes before the persisted user database is online, so EF's existence check
    // says "missing", CREATE DATABASE fails with 1801 and the process died. We retry ONLY this
    // startup call, on SQL warm-up errors. A global EnableRetryOnFailure is deliberately NOT
    // used: the many user-initiated BeginTransactionAsync call sites would break under an
    // execution strategy.
    public static async Task ApplyMigrationsAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("RentalPlatform.Migrations");

        await MigrationRetry.ExecuteAsync(
            async ct =>
            {
                // New scope + new context per attempt: a failed context must not be reused.
                await using var scope = services.CreateAsyncScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                // SQLite (used in integration tests) does not support EF migrations because the
                // migration scripts are SQL Server-specific. EnsureCreated builds an equivalent
                // schema directly from the model, which is sufficient for the test environment.
                if (dbContext.Database.ProviderName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) == true)
                    await dbContext.Database.EnsureCreatedAsync(ct);
                else
                    await dbContext.Database.MigrateAsync(ct);
            },
            MigrationRetry.IsStartupRetryable,
            MaxMigrationAttempts,
            (delay, ct) => Task.Delay(delay, ct),
            MigrationRetryDelay,
            (attempt, max, ex) => logger.LogWarning(
                "Startup migration attempt {Attempt}/{Max} failed (SQL error {SqlError}): {Message}. Retrying.",
                attempt, max, (ex as SqlException)?.Number, ex.Message),
            cancellationToken);
    }
}

internal static class MigrationRetry
{
    // 1801 = database already exists, 4060 = cannot open database, 18456 = login failed (startup).
    private static readonly HashSet<int> WarmUpErrorNumbers = [1801, 4060, 18456];

    public static bool IsStartupRetryable(Exception ex) =>
        ex is SqlException sql
        && (WarmUpErrorNumbers.Contains(sql.Number)
#pragma warning disable EF1001 // Internal EF Core API: the supported transient-error list, reused on purpose
            || SqlServerTransientExceptionDetector.ShouldRetryOn(sql));
#pragma warning restore EF1001

    public static async Task ExecuteAsync(
        Func<CancellationToken, Task> action,
        Func<Exception, bool> isRetryable,
        int maxAttempts,
        Func<TimeSpan, CancellationToken, Task> delay,
        TimeSpan delayBetween,
        Action<int, int, Exception> onRetry,
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await action(cancellationToken);
                return;
            }
            catch (Exception ex) when (attempt < maxAttempts && isRetryable(ex) && !cancellationToken.IsCancellationRequested)
            {
                onRetry(attempt, maxAttempts, ex);
                await delay(delayBetween, cancellationToken);
            }
        }
    }
}
