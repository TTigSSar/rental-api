using Microsoft.Extensions.Logging;
using RentalPlatform.Application.Abstractions;

namespace RentalPlatform.Infrastructure.Services;

/// <summary>
/// Development-only email service. Writes email content to the application log instead of
/// sending real messages. Replace with an SMTP / SES / SendGrid implementation for production.
/// </summary>
public sealed class DevelopmentEmailService : IEmailService
{
    private readonly ILogger<DevelopmentEmailService> _logger;

    public DevelopmentEmailService(ILogger<DevelopmentEmailService> logger)
    {
        _logger = logger;
    }

    public Task SendListingApprovedAsync(
        string ownerEmail,
        string ownerName,
        string listingTitle,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "[DEV-EMAIL] TO: {Email} | SUBJECT: Your toy listing has been approved\n" +
            "Hi {Name},\n\n" +
            "Great news! Your listing \"{Title}\" has been approved and is now publicly visible on the platform.\n\n" +
            "Parents can now find and book your toy. Thank you for listing with us!\n\n" +
            "— Child Toys Rental Team",
            ownerEmail, ownerName, listingTitle);

        return Task.CompletedTask;
    }

    public Task SendListingRejectedAsync(
        string ownerEmail,
        string ownerName,
        string listingTitle,
        string rejectionReason,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "[DEV-EMAIL] TO: {Email} | SUBJECT: Your toy listing was not approved\n" +
            "Hi {Name},\n\n" +
            "Unfortunately your listing \"{Title}\" was not approved.\n\n" +
            "Reason: {Reason}\n\n" +
            "If you have questions or would like to make changes and resubmit, please contact support.\n\n" +
            "— Child Toys Rental Team",
            ownerEmail, ownerName, listingTitle, rejectionReason);

        return Task.CompletedTask;
    }
}
