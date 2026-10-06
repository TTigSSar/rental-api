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
        var fileStorage = scope.ServiceProvider.GetRequiredService<IFileStorageService>();
        // Resolved from the same scope as the AppDbContext above, so the service's own store shares
        // this context — the seed's inserts are visible to it without another round trip.
        var homePointService = scope.ServiceProvider.GetRequiredService<IHomePointService>();
        var logger = scope.ServiceProvider
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger<DevelopmentSeedRunner>();

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("RentalPlatform-Seeder/1.0");

        var runner = new DevelopmentSeedRunner(dbContext, passwordHasher, logger, fileStorage, http, homePointService);
        await runner.RunAsync(cancellationToken);
    }
}
