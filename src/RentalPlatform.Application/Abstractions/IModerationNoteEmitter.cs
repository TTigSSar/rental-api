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
    /// <summary>A listing was rejected → a Reject note (subject = listing title, reason = the rejection reason label).</summary>
    Task ListingRejectedAsync(
        Guid moderatorId, Guid memberId, string listingTitle, string reasonLabel, string? note, CancellationToken cancellationToken = default);

    /// <summary>A listing was recategorised → a Category note (subject = listing title, reason = "{from} → {to}").</summary>
    Task ListingRecategorisedAsync(
        Guid moderatorId, Guid memberId, string listingTitle, string fromCategoryName, string toCategoryName, CancellationToken cancellationToken = default);

    /// <summary>An account was suspended → a Suspend note (reason = the optional suspend reason).</summary>
    Task AccountSuspendedAsync(
        Guid moderatorId, Guid memberId, string? reason, CancellationToken cancellationToken = default);
}
