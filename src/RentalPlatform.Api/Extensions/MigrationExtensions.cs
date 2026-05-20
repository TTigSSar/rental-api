using Microsoft.EntityFrameworkCore;
using RentalPlatform.Infrastructure.Persistence;

namespace RentalPlatform.Api.Extensions;

public static class MigrationExtensions
{
    // Applies pending EF Core migrations on startup. Runs in all environments so the
    // schema is current before any traffic is served and before the dev seed runs.
    public static async Task ApplyMigrationsAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // SQLite (used in integration tests) does not support EF migrations because the
        // migration scripts are SQL Server-specific. EnsureCreated builds an equivalent
        // schema directly from the model, which is sufficient for the test environment.
        if (dbContext.Database.ProviderName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) == true)
            await dbContext.Database.EnsureCreatedAsync(cancellationToken);
        else
            await dbContext.Database.MigrateAsync(cancellationToken);
    }
}
