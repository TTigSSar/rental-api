namespace RentalPlatform.Application.Abstractions;

/// <summary>
/// Abstraction for transactional email notifications.
/// Implementations MUST NOT throw — all failures must be handled and logged internally.
/// This contract allows callers in the Application layer to fire notifications without
/// wrapping them in try/catch or taking a dependency on ILogger.
/// </summary>
public interface IEmailService
{
    Task SendListingApprovedAsync(
        string ownerEmail,
        string ownerName,
        string listingTitle,
        CancellationToken cancellationToken = default);

    Task SendListingRejectedAsync(
        string ownerEmail,
        string ownerName,
        string listingTitle,
        string rejectionReason,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends the email-verification link (ADR-028/029). Localised by <paramref name="preferredLanguage"/>
    /// (en/hy/ru, English fallback). The body carries no user-controlled data, only the link.
    /// </summary>
    Task SendEmailVerificationAsync(
        string email,
        string? preferredLanguage,
        string link,
        CancellationToken cancellationToken = default);
}
