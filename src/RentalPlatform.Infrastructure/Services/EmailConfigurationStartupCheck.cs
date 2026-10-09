using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace RentalPlatform.Infrastructure.Services;

// Logs Critical ONCE at startup when Production cannot deliver email, and nothing else. It must
// never throw or block: the API stays up (other features work) and register/resend answer 503
// (ADR-028 section 9; M-016 - a startup throw in this stack is a full outage).
public sealed class EmailConfigurationStartupCheck : IHostedService
{
    private readonly EmailVerificationSettings _settings;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<EmailConfigurationStartupCheck> _logger;

    public EmailConfigurationStartupCheck(
        EmailVerificationSettings settings,
        IHostEnvironment environment,
        ILogger<EmailConfigurationStartupCheck> logger)
    {
        _settings = settings;
        _environment = environment;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_environment.IsProduction())
            {
                var missing = _settings.MissingRequirements();
                if (missing.Count > 0)
                {
                    _logger.LogCritical(
                        "Email verification is NOT operational in Production: {Missing}. " +
                        "POST /api/auth/register and /api/auth/resend-verification will answer 503 until this is fixed.",
                        string.Join("; ", missing));
                }
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Email configuration check failed; continuing startup.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
