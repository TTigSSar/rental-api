using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Infrastructure.DependencyInjection.DemoContentBootstrap;
using RentalPlatform.Infrastructure.Persistence;

namespace RentalPlatform.Infrastructure.DependencyInjection;

public static class DemoContentBootstrapExtensions
{
    // Reads Bootstrap:DemoContentEnabled / Bootstrap:DemoOwnerEmail / Bootstrap:DemoOwnerPassword
    // from configuration and, if enabled and both owner values are set, idempotently ensures a
    // showcase owner account and the catalogue's Approved listings (+ images) exist. Intended for
    // non-Development environments — Development already has a full catalogue via the dev seed.
    // Safe to call unconditionally: a no-op whenever DemoContentEnabled is false/unset, the owner
    // values are absent, or the content already exists.
    public static async Task BootstrapDemoContentAsync(
        this IServiceProvider services,
        IConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        var enabled = configuration.GetValue<bool>("Bootstrap:DemoContentEnabled");
        var ownerEmail = configuration["Bootstrap:DemoOwnerEmail"];
        var ownerPassword = configuration["Bootstrap:DemoOwnerPassword"];

        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var passwordHasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
        var fileStorage = scope.ServiceProvider.GetRequiredService<IFileStorageService>();
        var logger = scope.ServiceProvider
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger<DemoContentBootstrapRunner>();

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("RentalPlatform-DemoContentBootstrap/1.0");

        var runner = new DemoContentBootstrapRunner(dbContext, passwordHasher, fileStorage, http, logger);
        await runner.RunAsync(enabled, ownerEmail, ownerPassword, cancellationToken);
    }
}
