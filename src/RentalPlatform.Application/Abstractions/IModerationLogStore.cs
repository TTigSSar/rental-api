using RentalPlatform.Domain.Entities;

namespace RentalPlatform.Application.Abstractions;

/// <summary>
/// Append-only audit trail for admin moderation actions. Application services call
/// <see cref="AppendAsync"/> after a moderation write succeeds; nothing reads this back yet in
/// Phase 1 (the activity-feed endpoint arrives in a later phase), but the store abstraction is
/// shaped so that read methods can be added without touching callers.
/// </summary>
public interface IModerationLogStore
{
    Task AppendAsync(ModerationLogEntry entry, CancellationToken cancellationToken = default);
}
