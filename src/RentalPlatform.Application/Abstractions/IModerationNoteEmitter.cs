using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Application.Abstractions;

/// <summary>
/// Auto-posts a ModerationNote <see cref="Domain.Entities.ChatMessage"/> into a member's
/// Moderation conversation (get-or-created on demand) when an admin takes one of the actions
/// below. Every method is best-effort: a note failure is logged and swallowed so it can never
/// break the moderation action (reject/recategorise/suspend) that triggered it — mirrors
/// <see cref="IChatSystemMessageEmitter"/> exactly, including its try/catch-swallow + log doctrine.
/// </summary>
public interface IModerationNoteEmitter
{
    // Body and reason are rendered at emit time in the member's language (ADR-025 amendment
    // 2026-10-26): memberLanguage is the member's PreferredLanguage (null/unknown -> English).

    /// <summary>A listing was rejected → a Reject note (subject = listing title, reason = the rejection reason label in the member's language).</summary>
    Task ListingRejectedAsync(
        Guid moderatorId, Guid memberId, string? memberLanguage, string listingTitle, string reasonCode, string? note, CancellationToken cancellationToken = default);

    /// <summary>A listing was recategorised → a Category note (subject = listing title, reason = "{from} → {to}").</summary>
    Task ListingRecategorisedAsync(
        Guid moderatorId, Guid memberId, string? memberLanguage, string listingTitle, string fromCategoryName, string toCategoryName, CancellationToken cancellationToken = default);

    /// <summary>An account was suspended → a Suspend note (reason = the optional suspend reason, admin free text).</summary>
    Task AccountSuspendedAsync(
        Guid moderatorId, Guid memberId, string? memberLanguage, string? reason, CancellationToken cancellationToken = default);
}
