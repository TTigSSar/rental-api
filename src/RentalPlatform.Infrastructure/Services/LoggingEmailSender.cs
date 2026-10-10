using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RentalPlatform.Application.Abstractions;

namespace RentalPlatform.Infrastructure.Services;

// Development and test transport (ADR-029): writes the message to the log instead of sending it.
// The body of a verification email is a bearer link, so in Production it is never written down:
// there this logs only that a send was suppressed.
public sealed class LoggingEmailSender : IEmailSender
{
    private readonly IHostEnvironment _environment;
    private readonly ILogger<LoggingEmailSender> _logger;

    public LoggingEmailSender(IHostEnvironment environment, ILogger<LoggingEmailSender> logger)
    {
        _environment = environment;
        _logger = logger;
    }

    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        if (_environment.IsProduction())
        {
            _logger.LogWarning("[EMAIL] Send suppressed: the logging transport is configured in Production.");
            return Task.CompletedTask;
        }

        _logger.LogInformation(
            "[DEV-EMAIL] TO: {To} | SUBJECT: {Subject}\n{Body}",
            message.To, message.Subject, message.TextBody);

        return Task.CompletedTask;
    }
}
