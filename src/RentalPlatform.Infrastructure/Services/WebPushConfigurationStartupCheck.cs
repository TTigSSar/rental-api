using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace RentalPlatform.Infrastructure.Services;

// Logs Critical ONCE at startup when Web Push is not configured, and nothing else (ADR-067 section 14).
// It must never throw or block (M-016): push stays off, in-tab notifications keep working.
public sealed class WebPushConfigurationStartupCheck : IHostedService
{
    private readonly WebPushSettings _settings;
    private readonly ILogger<WebPushConfigurationStartupCheck> _logger;

    public WebPushConfigurationStartupCheck(
        WebPushSettings settings,
        ILogger<WebPushConfigurationStartupCheck> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var missing = _settings.MissingRequirements();
            if (missing.Count > 0)
            {
                _logger.LogCritical(
                    "Web Push is NOT configured: missing {Missing}. Push notifications are off and " +
                    "GET /api/push/public-key answers 404 until this is fixed.",
                    string.Join(", ", missing));
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Web Push configuration check failed; continuing startup.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
