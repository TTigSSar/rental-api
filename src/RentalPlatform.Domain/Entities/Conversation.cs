using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Domain.Entities;

/// <summary>
/// A chat thread. Two kinds (see <see cref="Kind"/>):
/// <list type="bullet">
/// <item>
/// <b>Booking</b> (the original ADR-001 shape): a 1:1 thread between the owner and the renter of
/// exactly one <see cref="Domain.Entities.Booking"/>. The status pill shown in the UI is derived
/// from the linked booking's status + <see cref="ClosedAt"/>; it is not stored.
/// </item>
/// <item>
/// <b>Moderation</b> (admin console): a booking-less thread between an admin moderator and a
/// member, opened from the admin Messages screen or auto-started by a moderation action (listing
/// rejected/recategorised, account suspended — see IModerationNoteEmitter). It has no
/// <see cref="Booking"/> and no toy strip (<see cref="ToyTitle"/>/<see cref="ToyImageUrl"/> stay
/// null), and never auto-closes (<see cref="ClosedAt"/> stays null forever — see
/// ChatService.TryLazyCloseAsync). <b>By convention, <see cref="OwnerId"/> is the moderator who
/// opened the thread and <see cref="RenterId"/> is the member</b> — this is not self-evident from
/// the field names, but it deliberately reuses the existing participant pair and the
/// (OwnerId, LastMessageAt) / (RenterId, LastMessageAt) indexes unchanged instead of adding a
/// parallel moderator/member column pair. One thread per member (not per moderator): a second
/// moderator continues the same conversation rather than opening a duplicate.
/// </item>
/// </list>
/// Inbox-preview fields are denormalised so the conversation list reads without joins — the same
/// pattern <see cref="Notification"/> uses.
/// </summary>
public sealed class Conversation
{
    public Guid Id { get; set; }

    /// <summary>
    /// The booking this conversation belongs to. Null for a <see cref="ConversationKind.Moderation"/>
    /// thread. Unique among non-null values (one Booking thread per booking) — see
    /// ConversationConfiguration's filtered unique index.
    /// </summary>
    public Guid? BookingId { get; set; }

    public ConversationKind Kind { get; set; }

    /// <summary>Listing owner for a Booking thread; the moderator who opened it for a Moderation thread.</summary>
    public Guid OwnerId { get; set; }

    /// <summary>Booking renter for a Booking thread; the member for a Moderation thread.</summary>
    public Guid RenterId { get; set; }

    // ── Toy strip (denormalised snapshot for the inbox row) ─────────────────────
    // Null for a Moderation thread — there is no toy to show.
    public string? ToyTitle { get; set; }
    public string? ToyImageUrl { get; set; }

    // ── Last-message preview (denormalised for join-free inbox ordering) ────────
    public Guid? LastMessageId { get; set; }
    public string? LastMessageSnippet { get; set; }
    public DateTime? LastMessageAt { get; set; }

    /// <summary>
    /// Set once the rental is complete AND both reviews are in ⇒ read-only. Always null for a
    /// Moderation thread — such a thread never auto-closes.
    /// </summary>
    public DateTime? ClosedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>Null for a Moderation thread.</summary>
    public Booking? Booking { get; set; }
    public User Owner { get; set; } = null!;
    public User Renter { get; set; } = null!;

    public ICollection<ChatMessage> Messages { get; set; } = new List<ChatMessage>();
    public ICollection<ConversationParticipant> Participants { get; set; } = new List<ConversationParticipant>();
}
