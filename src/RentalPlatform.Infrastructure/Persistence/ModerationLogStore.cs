using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Domain.Entities;

namespace RentalPlatform.Infrastructure.Persistence;

/// <summary>
/// Append-only audit trail writer. AppendAsync always runs after the caller's business mutation
/// has already been saved (see AdminReportsService/AdminListingsService/AdminUsersService/
/// AdminCategoriesService), so — same doctrine as IEmailService — a failure here must never
/// invert the client's view of a committed mutation. Failures are logged and swallowed rather
/// than propagated; the deliberate trade-off is that a moderation action can go unlogged if the
/// audit write itself fails (e.g. a future DetailJson overflow, an FK issue).
/// </summary>
public sealed class ModerationLogStore : IModerationLogStore
{
    private readonly AppDbContext _dbContext;
    private readonly ILogger<ModerationLogStore> _logger;

    public ModerationLogStore(AppDbContext dbContext, ILogger<ModerationLogStore> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task AppendAsync(ModerationLogEntry entry, CancellationToken cancellationToken = default)
    {
        try
        {
            _dbContext.ModerationLogEntries.Add(entry);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            // Detach so the failed entry doesn't linger in this scoped DbContext's change tracker
            // and get retried (and fail again) by an unrelated later SaveChangesAsync call in the
            // same request.
            _dbContext.Entry(entry).State = EntityState.Detached;

            _logger.LogError(
                exception,
                "Failed to append moderation log entry {Action} by actor {ActorUserId} for target {TargetType}/{TargetId} ({TargetLabel}).",
                entry.Action,
                entry.ActorUserId,
                entry.TargetType,
                entry.TargetId,
                entry.TargetLabel);
        }
    }
}
