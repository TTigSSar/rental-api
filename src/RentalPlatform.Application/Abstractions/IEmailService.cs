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
}
