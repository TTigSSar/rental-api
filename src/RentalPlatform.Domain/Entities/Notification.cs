using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Domain.Entities;

/// <summary>
/// An in-app notification delivered to a single recipient. Actor and toy display
/// data are denormalised (captured at emit time) so the feed reads with no joins
/// and system senders render uniformly — matching the "copy produced server-side"
/// contract the client relies on.
/// </summary>
public sealed class Notification
{
    public Guid Id { get; set; }

    /// <summary>The user who receives this notification. Every query filters on this.</summary>
    public Guid RecipientId { get; set; }

    public NotificationKind Kind { get; set; }
    public NotificationCategory Category { get; set; }

    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string? Meta { get; set; }

    /// <summary>Needs the user to act → "Action needed" pill + the Action filter.</summary>
    public bool Urgent { get; set; }

    // ── Actor (denormalised snapshot) ─────────────────────────────────────────
    public string ActorName { get; set; } = string.Empty;
    public string? ActorAvatarUrl { get; set; }
    public bool ActorVerified { get; set; }
    public bool ActorIsSystem { get; set; }
    /// <summary>PrimeIcon name (no pi- prefix) used for system senders.</summary>
    public string? ActorSystemIcon { get; set; }

    // ── Target ────────────────────────────────────────────────────────────────
    public NotificationEntityType EntityType { get; set; }
    public Guid EntityId { get; set; }
    /// <summary>In-app router path the card opens (e.g. /bookings/{id}).</summary>
    public string DeepLink { get; set; } = string.Empty;

    // ── Toy strip (denormalised) ───────────────────────────────────────────────
    public string? ToyTitle { get; set; }
    public string? ToyImageUrl { get; set; }

    // ── Actions ────────────────────────────────────────────────────────────────
    public string? PrimaryActionLabel { get; set; }
    public string? PrimaryActionDeepLink { get; set; }
    public string? SecondaryActionLabel { get; set; }
    public string? SecondaryActionDeepLink { get; set; }

    /// <summary>Null ⇒ unread.</summary>
    public DateTime? ReadAt { get; set; }
    public DateTime CreatedAt { get; set; }

    public User Recipient { get; set; } = null!;
}
