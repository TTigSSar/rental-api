using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Infrastructure.DependencyInjection.DemoContentBootstrap;
using RentalPlatform.Infrastructure.Persistence;

namespace RentalPlatform.Infrastructure.DependencyInjection;

public static class DemoContentBootstrapExtensions
{
    internal const string DemoContentEnabledKey = "Bootstrap:DemoContentEnabled";

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
        var ownerEmail = configuration["Bootstrap:DemoOwnerEmail"];
        var ownerPassword = configuration["Bootstrap:DemoOwnerPassword"];

        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var passwordHasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
        var fileStorage = scope.ServiceProvider.GetRequiredService<IFileStorageService>();
        var logger = scope.ServiceProvider
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger<DemoContentBootstrapRunner>();

        var enabled = ParseDemoContentEnabled(configuration, logger);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("RentalPlatform-DemoContentBootstrap/1.0");

        var runner = new DemoContentBootstrapRunner(dbContext, passwordHasher, fileStorage, http, logger);
        await runner.RunAsync(enabled, ownerEmail, ownerPassword, cancellationToken);
    }

    // Bootstrap:DemoContentEnabled drives a purely optional, cosmetic feature (an initial public
    // catalogue) — a bad value for it must degrade to "feature off", never to "app dead". This
    // matters because Docker Compose does not omit unset .env variables: it maps them to an empty
    // string and still sets the key, so a plain `configuration.GetValue<bool>(...)` (or the
    // `TypeConverter`-based conversion `GetValue` does internally) throws InvalidOperationException
    // on "" just as it would on a typo like "yes" — and since this runs on the startup path before
    // the app is marked healthy, that throw is a full-site crashloop, not a degraded feature.
    //
    // Accepted values are exactly "true"/"false" (case-insensitive, via bool.TryParse) — the same
    // contract .env.production.example documents. A wider truthy set ("1"/"yes"/"on") was
    // considered and rejected: it would silently multiply the ways to spell "enabled", inviting the
    // exact typo-tolerance illusion that caused this bug in the first place (e.g. "no" reads as a
    // sensible "false" spelling but wouldn't match "yes"/"1"/"on" as true's counterpart, so the
    // symmetry breaks down fast). Keeping the accepted set to two exact spellings keeps this
    // WARNING's guidance unambiguous.
    //
    // Empty/absent is the expected, silent steady state (most deployments never set this key).
    // Anything else that isn't "true"/"false" is almost certainly an operator typo, so it gets a
    // WARNING naming the bad value and the fact that demo content is therefore disabled — silently
    // ignoring a typo would leave Tigran wondering why nothing happened.
    internal static bool ParseDemoContentEnabled(IConfiguration configuration, ILogger logger)
    {
        var raw = configuration[DemoContentEnabledKey];

        if (string.IsNullOrEmpty(raw))
        {
            return false;
        }

        if (bool.TryParse(raw, out var parsed))
        {
            return parsed;
        }

        logger.LogWarning(
            "Configuration key '{ConfigKey}' has an unrecognized value '{ConfigValue}' — only 'true' or " +
            "'false' (case-insensitive) are accepted. Demo content bootstrap is DISABLED for this run.",
            DemoContentEnabledKey,
            raw);
        return false;
    }
}
