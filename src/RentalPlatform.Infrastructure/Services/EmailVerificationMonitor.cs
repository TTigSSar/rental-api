using Microsoft.Extensions.Logging;
using RentalPlatform.Application.Abstractions;

namespace RentalPlatform.Infrastructure.Services;

public sealed class EmailVerificationMonitor : IEmailVerificationMonitor
{
    private readonly ILogger<EmailVerificationMonitor> _logger;

    public EmailVerificationMonitor(ILogger<EmailVerificationMonitor> logger) => _logger = logger;

    // Warning, stable template, user id only: a rising rate of these is somebody re-registering one
    // address to mail its owner (ADR-028 amendment), and the id lets an operator look the account up
    // without the log ever holding an address or a token.
    public void PerRecipientCapReached(Guid userId) =>
        _logger.LogWarning("Email verification per-recipient cap reached for user {UserId}.", userId);
}
