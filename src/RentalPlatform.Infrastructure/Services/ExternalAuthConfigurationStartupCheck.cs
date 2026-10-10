using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RentalPlatform.Infrastructure.Services;

// Logs Critical ONCE at startup when Production has no Google client id, and nothing else. It must
// never throw or block (M-016): the API stays up, password sign-in keeps working, and Google
// sign-in answers 503 auth.external_provider_unavailable (ADR-030 section 7).
public sealed class ExternalAuthConfigurationStartupCheck : IHostedService
{
    private readonly ExternalAuthOptions _options;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<ExternalAuthConfigurationStartupCheck> _logger;

    public ExternalAuthConfigurationStartupCheck(
        IOptions<ExternalAuthOptions> options,
        IHostEnvironment environment,
        ILogger<ExternalAuthConfigurationStartupCheck> logger)
    {
        _options = options.Value;
        _environment = environment;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_environment.IsProduction() && !_options.Google.IsConfigured)
            {
                _logger.LogCritical(
                    "Google sign-in is NOT operational in Production: ExternalAuth:Google:ValidAudiences has no real client id. " +
                    "POST /api/auth/external (google) and /api/auth/external/nonce will answer 503 until GOOGLE_CLIENT_ID is set.");
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "External auth configuration check failed; continuing startup.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
