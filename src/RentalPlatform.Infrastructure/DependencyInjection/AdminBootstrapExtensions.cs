using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Infrastructure.DependencyInjection.AdminBootstrap;

namespace RentalPlatform.Infrastructure.DependencyInjection;

public static class AdminBootstrapExtensions
{
    // Reads Bootstrap:AdminEmail / Bootstrap:AdminPassword from configuration and, if both are
    // set, idempotently ensures an Admin user exists for them. Intended for non-Development
    // environments, where (unlike Development) there is no seed data and therefore no admin
    // account at all on a fresh database. Safe to call unconditionally: a no-op whenever the
    // two config values are absent, or whenever the account already exists.
    public static async Task BootstrapAdminAsync(
        this IServiceProvider services,
        IConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        var adminEmail = configuration["Bootstrap:AdminEmail"];
        var adminPassword = configuration["Bootstrap:AdminPassword"];

        await using var scope = services.CreateAsyncScope();
        var userAuthStore = scope.ServiceProvider.GetRequiredService<IUserAuthStore>();
        var passwordHasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
        var logger = scope.ServiceProvider
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger<AdminBootstrapRunner>();

        var runner = new AdminBootstrapRunner(userAuthStore, passwordHasher, logger);
        await runner.RunAsync(adminEmail, adminPassword, cancellationToken);
    }
}
