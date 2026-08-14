using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Domain.Entities;

/// <summary>
/// Audit-trail row for an admin moderation action (listing approve/reject/recategorise, and —
/// in later phases — category and user/report moderation). TargetLabel is a denormalised,
/// human-readable snapshot (e.g. the listing title) so the activity feed still reads sensibly
/// even after the target row itself is deleted.
/// </summary>
public sealed class ModerationLogEntry
{
    public Guid Id { get; set; }
    public Guid ActorUserId { get; set; }
    public ModerationAction Action { get; set; }
    public ModerationTargetType TargetType { get; set; }
    public Guid TargetId { get; set; }
    public string TargetLabel { get; set; } = string.Empty;
    public string? DetailJson { get; set; }
    public DateTime CreatedAt { get; set; }

    public User Actor { get; set; } = null!;
}
