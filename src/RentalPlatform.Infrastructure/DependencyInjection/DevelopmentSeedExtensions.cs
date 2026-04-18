using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Infrastructure.DependencyInjection.DevelopmentSeed;
using RentalPlatform.Infrastructure.Persistence;

namespace RentalPlatform.Infrastructure.DependencyInjection;

public static class DevelopmentSeedExtensions
{
    public static async Task SeedDevelopmentDataAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var passwordHasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
        var logger = scope.ServiceProvider
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger<DevelopmentSeedRunner>();

        var runner = new DevelopmentSeedRunner(dbContext, passwordHasher, logger);
        await runner.RunAsync(cancellationToken);
    }
}
