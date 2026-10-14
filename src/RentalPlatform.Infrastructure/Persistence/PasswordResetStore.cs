using Microsoft.EntityFrameworkCore;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Infrastructure.Persistence;

// The atomic writes behind password reset (ADR-035 section 2). Same discipline as
// EmailVerificationStore: lock order Users then UserTokens, ExecuteUpdate (never the change tracker)
// for the conditional writes, unique-index violations mapped to a defined outcome.
public sealed class PasswordResetStore : IPasswordResetStore
{
    private readonly AppDbContext _dbContext;

    public PasswordResetStore(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IssueResetTokenOutcome> TryIssueAsync(
        UserToken token, DateTime now, TokenLimits limits, CancellationToken cancellationToken = default)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            // Users first. The predicate doubles as a guard: no token is issued to an account that was
            // blocked (or is not verified) a moment after the caller looked. The no-op SET still takes
            // the row's exclusive lock, which serialises this against a concurrent reset.
            var locked = await _dbContext.Users
                .Where(user => user.Id == token.UserId && user.IsEmailConfirmed && !user.IsBlocked)
                .ExecuteUpdateAsync(set => set.SetProperty(user => user.CreatedAt, user => user.CreatedAt), cancellationToken);
            if (locked != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return IssueResetTokenOutcome.Refused;
            }

            // Race-free now that the user row is locked.
            var issued = await _dbContext.UserTokens.CountAsync(
                candidate => candidate.UserId == token.UserId
                             && candidate.Purpose == TokenPurpose.PasswordReset
                             && candidate.CreatedAt >= limits.CapWindowStart,
                cancellationToken);
            if (issued >= limits.MaxTokens)
            {
                await transaction.RollbackAsync(cancellationToken);
                return IssueResetTokenOutcome.Refused;
            }

            // Only live tokens older than the cooldown are revoked (revoke = ConsumedAt, never delete).
            // A younger live one survives, so the filtered unique index rejects the insert below: that
            // is the cooldown, enforced by the database, and what makes two concurrent requests safe.
            await _dbContext.UserTokens
                .Where(candidate => candidate.UserId == token.UserId
                                    && candidate.Purpose == TokenPurpose.PasswordReset
                                    && candidate.ConsumedAt == null
                                    && candidate.CreatedAt < limits.CooldownCutoff)
                .ExecuteUpdateAsync(set => set.SetProperty(candidate => candidate.ConsumedAt, (DateTime?)now), cancellationToken);

            _dbContext.UserTokens.Add(token);
            await _dbContext.SaveChangesAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return IssueResetTokenOutcome.Issued;
        }
        catch (Exception exception) when (EmailVerificationStore.IsUniqueViolation(exception))
        {
            _dbContext.Entry(token).State = EntityState.Detached;
            await transaction.RollbackAsync(CancellationToken.None);
            return IssueResetTokenOutcome.Refused;
        }
    }

    public async Task<bool> TryCommitResetAsync(
        Guid tokenId, Guid userId, string newPasswordHash, DateTime now, CancellationToken cancellationToken = default)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        // Users first (lock order). Blocked or unverified accounts are refused here even if they
        // became so after the caller's read; a reset never verifies a pending account (ADR-035 section 4).
        var updated = await _dbContext.Users
            .Where(user => user.Id == userId && !user.IsBlocked && user.IsEmailConfirmed)
            .ExecuteUpdateAsync(set => set.SetProperty(user => user.PasswordHash, newPasswordHash), cancellationToken);

        if (updated != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        var consumed = await _dbContext.UserTokens
            .Where(token => token.Id == tokenId
                            && token.Purpose == TokenPurpose.PasswordReset
                            && token.ConsumedAt == null
                            && token.ExpiresAt > now)
            .ExecuteUpdateAsync(set => set.SetProperty(token => token.ConsumedAt, (DateTime?)now), cancellationToken);

        if (consumed != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        await transaction.CommitAsync(cancellationToken);

        // Nothing tracked can be trusted after a bulk UPDATE: the caller re-reads the user fresh.
        _dbContext.ChangeTracker.Clear();
        return true;
    }
}
