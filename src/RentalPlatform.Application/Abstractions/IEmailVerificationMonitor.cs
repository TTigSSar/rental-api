namespace RentalPlatform.Application.Abstractions;

/// <summary>
/// Operational signals of the verification flow (ADR-028). The Application layer has no logging
/// abstraction by design, so the signal goes through this seam and Infrastructure writes the log.
/// Implementations MUST NOT throw and MUST NOT receive an email address or a token.
/// </summary>
public interface IEmailVerificationMonitor
{
    /// <summary>
    /// A re-registration was refused because the recipient already has the maximum verification
    /// emails for the window. Distinguishes cap refusals from plain cooldown 429s for monitoring.
    /// </summary>
    void PerRecipientCapReached(Guid userId);
}
