namespace RentalPlatform.Domain.Entities;

/// <summary>
/// A 1:1 chat thread between the owner and the renter of a single booking.
/// Keyed to exactly one <see cref="Booking"/> (see ADR-001). The status pill shown
/// in the UI is derived from the linked booking's status + <see cref="ClosedAt"/>;
/// it is not stored. Inbox-preview fields are denormalised so the conversation list
/// reads without joins — the same pattern <see cref="Notification"/> uses.
/// </summary>
public sealed class Conversation
{
    public Guid Id { get; set; }

    /// <summary>The booking this conversation belongs to (unique — one thread per booking).</summary>
    public Guid BookingId { get; set; }

    /// <summary>Listing owner. One of the two participants.</summary>
    public Guid OwnerId { get; set; }

    /// <summary>Booking renter. The other participant.</summary>
    public Guid RenterId { get; set; }

    // ── Toy strip (denormalised snapshot for the inbox row) ─────────────────────
    public string ToyTitle { get; set; } = string.Empty;
    public string? ToyImageUrl { get; set; }

    // ── Last-message preview (denormalised for join-free inbox ordering) ────────
    public Guid? LastMessageId { get; set; }
    public string? LastMessageSnippet { get; set; }
    public DateTime? LastMessageAt { get; set; }

    /// <summary>Set once the rental is complete AND both reviews are in ⇒ read-only.</summary>
    public DateTime? ClosedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public Booking Booking { get; set; } = null!;
    public User Owner { get; set; } = null!;
    public User Renter { get; set; } = null!;

    public ICollection<ChatMessage> Messages { get; set; } = new List<ChatMessage>();
    public ICollection<ConversationParticipant> Participants { get; set; } = new List<ConversationParticipant>();
}
