using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Application.DTOs;

/// <summary>Admin console Phase 5: GET /api/admin/overview/activity, newest first.</summary>
public sealed class AdminActivityFeedResponse
{
    public IReadOnlyCollection<AdminActivityItemResponse> Items { get; init; } = Array.Empty<AdminActivityItemResponse>();
}

/// <summary>
/// One ModerationLogEntry row. The client composes the display line (e.g. "Sona K. approved STEM
/// discovery lab kit") from Action + TargetLabel + DetailJson — nothing here is a pre-built
/// display string, so it stays localisable. ActorFirstName/ActorLastName/ActorAvatarUrl are null
/// when the actor user row no longer exists; the entry itself is still returned (ActorId, Action,
/// TargetLabel, etc. survive) so the audit trail never develops a hole.
/// </summary>
public sealed class AdminActivityItemResponse
{
    public Guid Id { get; init; }
    public ModerationAction Action { get; init; }
    public ModerationTargetType TargetType { get; init; }
    public Guid TargetId { get; init; }
    public string TargetLabel { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }

    public Guid ActorId { get; init; }
    public string? ActorFirstName { get; init; }
    public string? ActorLastName { get; init; }
    public string? ActorAvatarUrl { get; init; }

    public string? DetailJson { get; init; }
}
