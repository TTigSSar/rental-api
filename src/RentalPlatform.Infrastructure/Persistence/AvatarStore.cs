using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Domain.Entities;

namespace RentalPlatform.Infrastructure.Persistence;

// ADR-070 point 9. One conditional UPDATE inside a transaction, in two SQL forms: AvatarUrl IS NULL when the
// caller saw no avatar, AvatarUrl = @expectedOld otherwise (a null parameter would make EF emit a different,
// slower null-compensation form, so the two are written out).
public sealed class AvatarStore : IAvatarStore
{
    public static readonly EventId AvatarCommitAmbiguousEvent = new(6902, "AvatarCommitAmbiguous");

    private readonly AppDbContext _dbContext;
    private readonly ILogger<AvatarStore>? _logger;

    public AvatarStore(AppDbContext dbContext, ILogger<AvatarStore>? logger = null)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<AvatarUserState?> GetStateAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var row = await _dbContext.Users
            .AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => new { user.AvatarUrl, Deleted = user.DeletedAt != null, user.IsBlocked })
            .FirstOrDefaultAsync(cancellationToken);

        return row is null ? null : new AvatarUserState(row.AvatarUrl, row.Deleted, row.IsBlocked);
    }

    // Exposed for the ToQueryString() tests (B-U: the two forms) and the SQL Server tests.
    public static IQueryable<User> CasQuery(AppDbContext dbContext, Guid userId, string? expectedOld)
    {
        var active = dbContext.Users.Where(user => user.Id == userId && user.DeletedAt == null && !user.IsBlocked);
        return expectedOld is null
            ? active.Where(user => user.AvatarUrl == null)
            : active.Where(user => user.AvatarUrl == expectedOld);
    }

    public async Task<AvatarCasOutcome> TryReplaceAsync(Guid userId, string? expectedOld, string? newUrl, CancellationToken cancellationToken = default)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        var rows = await CasQuery(_dbContext, userId, expectedOld)
            .ExecuteUpdateAsync(set => set.SetProperty(user => user.AvatarUrl, newUrl), cancellationToken);

        if (rows == 1)
        {
            try
            {
                await transaction.CommitAsync(cancellationToken);
                return AvatarCasOutcome.Committed;
            }
            catch (Exception exception)
            {
                // The server may or may not have applied it. The caller keeps the new file and deletes
                // nothing; the orphan sweep reconciles later.
                _logger?.LogError(AvatarCommitAmbiguousEvent, exception, "Avatar commit outcome unknown for user {UserId}.", userId);
                return AvatarCasOutcome.CommitAmbiguous;
            }
        }

        await transaction.RollbackAsync(cancellationToken);

        var state = await GetStateAsync(userId, cancellationToken);
        if (state is null || state.IsDeleted)
        {
            return AvatarCasOutcome.UserDeleted;
        }

        return state.IsBlocked ? AvatarCasOutcome.UserBlocked : AvatarCasOutcome.Mismatch;
    }

    public void MirrorCommitted(User user, string? newUrl)
    {
        // Same pattern as UserAuthStore.TryChangePasswordAsync: show the saved value without leaving the
        // entity Modified (a later SaveChanges must not re-send it).
        var entry = _dbContext.Entry(user);
        entry.Property(candidate => candidate.AvatarUrl).CurrentValue = newUrl;
        entry.Property(candidate => candidate.AvatarUrl).OriginalValue = newUrl;
        entry.Property(candidate => candidate.AvatarUrl).IsModified = false;
    }
}
