using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Application.DTOs;

/// <summary>
/// One row of the admin Messages thread queue — also the shape returned by
/// POST /api/admin/messages/threads (get-or-create). Status/MarketplaceRole are derived exactly
/// as AdminUsersService.MapToSummary derives them (from IsBlocked/IsIdConfirmed and
/// listing/rental counts) — nothing new persisted.
/// </summary>
public sealed class AdminMessageThreadResponse
{
    public Guid ConversationId { get; init; }

    public Guid MemberId { get; init; }
    public string MemberFirstName { get; init; } = string.Empty;
    public string MemberLastName { get; init; } = string.Empty;
    public string? MemberAvatarUrl { get; init; }

    // Derived: IsBlocked => Suspended; else IsIdConfirmed => Active; else Pending.
    public UserAccountStatus MemberStatus { get; init; }
    public bool MemberIsIdConfirmed { get; init; }

    // Derived from activity: has listings => Owner, has bookings-as-renter => Renter, both =>
    // Both, neither => Renter.
    public MarketplaceRole MemberMarketplaceRole { get; init; }

    // Count of Open reports filed against this member.
    public int MemberOpenFlagCount { get; init; }

    public int UnreadCount { get; init; }
    public string? LastMessageSnippet { get; init; }
    public DateTime? LastMessageAt { get; init; }

    /// <summary>
    /// "text" | "image" | "system" | "moderationNote" token for the thread's last message (see
    /// <c>ChatTokens.MessageTypeToken</c>) — same convention as <c>ChatConversationResponse.LastMessageType</c>.
    /// Null when there is no last message yet.
    /// </summary>
    public string? LastMessageType { get; init; }

    /// <summary>
    /// The moderation note's subject when <see cref="LastMessageType"/> is "moderationNote"; null
    /// otherwise. <see cref="LastMessageSnippet"/> is the raw note body in that case — this lets the
    /// client render "Note: {subject}" instead of bare body text.
    /// </summary>
    public string? LastMessageNoteSubject { get; init; }

    /// <summary>True when the last message in this thread was sent by the member — drives the "Needs reply" filter.</summary>
    public bool NeedsReply { get; init; }

    public DateTime CreatedAt { get; init; }
}
