using RentalPlatform.Application.DTOs;

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

    /// <summary>
    /// Sends the password-reset link (ADR-035). Same contract as the verification email: en/hy/ru with
    /// an English fallback, no user-controlled data in the body, never throws.
    /// </summary>
    Task SendPasswordResetAsync(
        string email,
        string? preferredLanguage,
        string link,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends the "your account was deleted" confirmation (ADR-056 section 4) to the pre-erasure address, in
    /// the pre-erasure language (en/hy/ru, English fallback). The body carries no user data. Never throws.
    /// </summary>
    Task SendAccountDeletedAsync(
        string email,
        string? preferredLanguage,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends the "new message in your chat" email (ADR-059): en/hy/ru with an English fallback, no message
    /// text, links built by the caller. Never throws.
    /// </summary>
    Task SendChatUnreadAsync(
        ChatUnreadEmail email,
        CancellationToken cancellationToken = default);
}
