using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.DTOs;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Infrastructure.Persistence;

// Every conditional UPDATE, transaction and unique-violation translation behind email verification
// (ADR-028 section 4). Lock order is always Users first, then UserTokens, in every writer, so two
// writers can queue on a row but never deadlock on each other.
//
// ExecuteUpdate bypasses the change tracker by design: a tracked SaveChanges would write back
// whatever stale values the tracker holds (the replacement and external-reset paths would silently
// resurrect the pending account's old password).
public sealed class EmailVerificationStore : IEmailVerificationStore
{
    private readonly AppDbContext _dbContext;

    public EmailVerificationStore(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<AccountState?> FindAccountStateAsync(string email, CancellationToken cancellationToken = default) =>
        _dbContext.Users
            .AsNoTracking()
            .Where(user => user.Email == email)
            .Select(user => new AccountState(user.Id, user.IsEmailConfirmed, user.IsBlocked, user.PreferredLanguage))
            .FirstOrDefaultAsync(cancellationToken);

    public Task<DateTime?> GetLatestTokenCreatedAtAsync(
        Guid userId, TokenPurpose purpose, CancellationToken cancellationToken = default) =>
        _dbContext.UserTokens
            .AsNoTracking()
            .Where(token => token.UserId == userId && token.Purpose == purpose)
            .OrderByDescending(token => token.CreatedAt)
            .Select(token => (DateTime?)token.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

    public Task<int> CountTokensCreatedSinceAsync(
        Guid userId, TokenPurpose purpose, DateTime since, CancellationToken cancellationToken = default) =>
        _dbContext.UserTokens
            .AsNoTracking()
            .CountAsync(token => token.UserId == userId && token.Purpose == purpose && token.CreatedAt >= since, cancellationToken);

    public async Task<bool> TryAddUserAsync(User user, UserToken? token, CancellationToken cancellationToken = default)
    {
        _dbContext.Users.Add(user);
        if (token is not null)
        {
            _dbContext.UserTokens.Add(token);
        }

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            Detach(token);
            Detach(user);
            return false;
        }
    }

    public async Task<ReplacePendingOutcome> TryReplacePendingAsync(
        Guid userId, User candidate, UserToken token, DateTime now, TokenLimits limits,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            // Users first. The predicate is the whole point: a verified or blocked account is never
            // replaced, even if it became one a millisecond after the caller looked.
            var replaced = await _dbContext.Users
                .Where(user => user.Id == userId && !user.IsEmailConfirmed && !user.IsBlocked)
                .ExecuteUpdateAsync(
                    set => set
                        .SetProperty(user => user.PasswordHash, candidate.PasswordHash)
                        .SetProperty(user => user.FirstName, candidate.FirstName)
                        .SetProperty(user => user.LastName, candidate.LastName)
                        .SetProperty(user => user.PhoneNumber, candidate.PhoneNumber)
                        .SetProperty(user => user.PreferredLanguage, candidate.PreferredLanguage)
                        .SetProperty(user => user.CreatedAt, now)
                        .SetProperty(user => user.HomeLatitude, (decimal?)null)
                        .SetProperty(user => user.HomeLongitude, (decimal?)null)
                        .SetProperty(user => user.HomePublicLatitude, (decimal?)null)
                        .SetProperty(user => user.HomePublicLongitude, (decimal?)null)
                        .SetProperty(user => user.HomeDistrictId, (Guid?)null)
                        .SetProperty(user => user.HomePointUpdatedAt, (DateTime?)null),
                    cancellationToken);

            if (replaced != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return ReplacePendingOutcome.NotReplaceable;
            }

            // The Users row is now X-locked by this transaction, so another writer for the same user
            // queues behind it: the cap below is counted race-free (ADR-028 amendment 2026-10-09).
            if (await OverCapAsync(userId, token.Purpose, limits, cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return ReplacePendingOutcome.OverCap;
            }

            // Only tokens older than the cooldown are revoked (same rule as resend). A fresh token
            // written by a concurrent registration or resend survives, so the filtered unique index
            // rejects the insert below, this transaction rolls back (the Users replacement with it)
            // and the caller answers 429 instead of sending a second email.
            await RevokeActiveTokensAsync(userId, token.Purpose, now, limits.CooldownCutoff, cancellationToken);

            _dbContext.UserTokens.Add(token);
            await _dbContext.SaveChangesAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return ReplacePendingOutcome.Replaced;
        }
        catch (Exception exception) when (IsUniqueViolation(exception))
        {
            Detach(token);
            await transaction.RollbackAsync(CancellationToken.None);
            return ReplacePendingOutcome.TokenConflict;
        }
    }

    public async Task<RotateTokenOutcome> TryRotateTokenAsync(
        UserToken token, DateTime now, TokenLimits limits, CancellationToken cancellationToken = default)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            // Lock order is Users then UserTokens in EVERY writer. This one only changes tokens, but
            // inserting a token takes a shared lock on its user row for the foreign-key check, which
            // would put it in the reverse order against a concurrent verify (X on the user, then on
            // the token). Touching the user row first removes that cycle. The predicate doubles as a
            // guard: a token is never issued to an account that was verified or blocked in the meantime.
            var locked = await _dbContext.Users
                .Where(user => user.Id == token.UserId && !user.IsEmailConfirmed && !user.IsBlocked)
                .ExecuteUpdateAsync(set => set.SetProperty(user => user.CreatedAt, user => user.CreatedAt), cancellationToken);
            if (locked != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return RotateTokenOutcome.Conflict;
            }

            if (await OverCapAsync(token.UserId, token.Purpose, limits, cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return RotateTokenOutcome.Conflict;
            }

            // Only tokens older than the cooldown are revoked. If a younger active token exists the
            // filtered unique index (UserId, Purpose) rejects the insert below: that is the cooldown,
            // enforced by the database, and it is also what makes two concurrent resends safe.
            await RevokeActiveTokensAsync(token.UserId, token.Purpose, now, limits.CooldownCutoff, cancellationToken);

            _dbContext.UserTokens.Add(token);
            await _dbContext.SaveChangesAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return RotateTokenOutcome.Rotated;
        }
        catch (Exception exception) when (IsUniqueViolation(exception))
        {
            Detach(token);
            await transaction.RollbackAsync(CancellationToken.None);
            return RotateTokenOutcome.Conflict;
        }
    }

    // Two single-table reads on purpose, NOT one join. A join reads the token row and then the user
    // row while still holding its shared lock on the token (READ COMMITTED), which is the opposite of
    // the writers' lock order (Users then UserTokens) and was observed to deadlock against a
    // concurrent verify. One statement holds one lock at a time, so a reader can never be half of a cycle.
    public async Task<VerificationTokenView?> FindTokenAsync(
        byte[] tokenHash, TokenPurpose purpose, CancellationToken cancellationToken = default)
    {
        var token = await _dbContext.UserTokens
            .AsNoTracking()
            .Where(candidate => candidate.TokenHash == tokenHash && candidate.Purpose == purpose)
            .Select(candidate => new { candidate.Id, candidate.UserId, candidate.ExpiresAt, candidate.ConsumedAt })
            .FirstOrDefaultAsync(cancellationToken);
        if (token is null)
        {
            return null;
        }

        var user = await _dbContext.Users
            .AsNoTracking()
            .Where(candidate => candidate.Id == token.UserId)
            .Select(candidate => new { candidate.IsEmailConfirmed, candidate.IsBlocked, candidate.PasswordHash })
            .FirstOrDefaultAsync(cancellationToken);
        if (user is null)
        {
            return null;
        }

        return new VerificationTokenView(
            token.Id, token.UserId, token.ExpiresAt, token.ConsumedAt,
            user.IsEmailConfirmed, user.IsBlocked, user.PasswordHash);
    }

    public async Task<bool> TryCommitVerificationAsync(
        Guid tokenId, Guid userId, TokenPurpose purpose, string seenPasswordHash, DateTime now,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        // Users first (lock order). PasswordHash = @seenHash is what stops a replacement that landed
        // after the caller's BCrypt check from leaving a VERIFIED account holding someone else's password.
        var verified = await _dbContext.Users
            .Where(user => user.Id == userId
                           && !user.IsEmailConfirmed
                           && !user.IsBlocked
                           && user.PasswordHash == seenPasswordHash)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(user => user.IsEmailConfirmed, true)
                    .SetProperty(user => user.EmailConfirmedAt, (DateTime?)now),
                cancellationToken);

        if (verified != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        var consumed = await _dbContext.UserTokens
            .Where(token => token.Id == tokenId
                            && token.Purpose == purpose
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

    public async Task<bool> TryResetPendingForExternalAsync(
        Guid userId, ExternalUserInfo external, string firstName, string lastName, DateTime now,
        CancellationToken cancellationToken = default)
    {
        var provider = external.Provider.ToLowerInvariant();
        var avatarUrl = string.IsNullOrWhiteSpace(external.AvatarUrl) ? null : external.AvatarUrl.Trim();

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            // The provider has proven the mailbox, so the pending registration is replaced wholesale:
            // no password, no phone, no home point and no profile survives from whoever squatted the
            // address (ADR-028 section 2).
            var reset = await _dbContext.Users
                .Where(user => user.Id == userId && !user.IsEmailConfirmed && !user.IsBlocked)
                .ExecuteUpdateAsync(
                    set => set
                        .SetProperty(user => user.PasswordHash, string.Empty)
                        .SetProperty(user => user.FirstName, firstName)
                        .SetProperty(user => user.LastName, lastName)
                        .SetProperty(user => user.PhoneNumber, (string?)null)
                        .SetProperty(user => user.PreferredLanguage, (string?)null)
                        .SetProperty(user => user.ExternalAuthProvider, (string?)provider)
                        .SetProperty(user => user.ExternalProviderId, (string?)external.ProviderUserId)
                        .SetProperty(user => user.AvatarUrl, avatarUrl)
                        .SetProperty(user => user.CreatedAt, now)
                        .SetProperty(user => user.IsEmailConfirmed, true)
                        .SetProperty(user => user.EmailConfirmedAt, (DateTime?)now)
                        .SetProperty(user => user.HomeLatitude, (decimal?)null)
                        .SetProperty(user => user.HomeLongitude, (decimal?)null)
                        .SetProperty(user => user.HomePublicLatitude, (decimal?)null)
                        .SetProperty(user => user.HomePublicLongitude, (decimal?)null)
                        .SetProperty(user => user.HomeDistrictId, (Guid?)null)
                        .SetProperty(user => user.HomePointUpdatedAt, (DateTime?)null),
                    cancellationToken);

            if (reset != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }

            await RevokeActiveTokensAsync(userId, TokenPurpose.EmailVerification, now, null, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        catch (Exception exception) when (IsUniqueViolation(exception))
        {
            // The (provider, id) pair got linked to another account in the meantime.
            await transaction.RollbackAsync(CancellationToken.None);
            return false;
        }
        finally
        {
            // The caller loaded this user tracked before resetting it; that snapshot is now stale.
            _dbContext.ChangeTracker.Clear();
        }
    }

    // olderThan = null revokes every active token (external reset); otherwise only those created before it.
    private Task<int> RevokeActiveTokensAsync(
        Guid userId, TokenPurpose purpose, DateTime now, DateTime? olderThan, CancellationToken cancellationToken) =>
        _dbContext.UserTokens
            .Where(token => token.UserId == userId
                            && token.Purpose == purpose
                            && token.ConsumedAt == null
                            && (olderThan == null || token.CreatedAt < olderThan))
            .ExecuteUpdateAsync(set => set.SetProperty(token => token.ConsumedAt, (DateTime?)now), cancellationToken);

    private async Task<bool> OverCapAsync(
        Guid userId, TokenPurpose purpose, TokenLimits limits, CancellationToken cancellationToken) =>
        await _dbContext.UserTokens.CountAsync(
            token => token.UserId == userId && token.Purpose == purpose && token.CreatedAt >= limits.CapWindowStart,
            cancellationToken) >= limits.MaxTokens;

    public Task<int> DiscardHomePointIfAccountChangedAsync(
        Guid userId, string registrantPasswordHash, CancellationToken cancellationToken = default) =>
        _dbContext.Users
            .Where(user => user.Id == userId && user.PasswordHash != registrantPasswordHash)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(user => user.HomeLatitude, (decimal?)null)
                    .SetProperty(user => user.HomeLongitude, (decimal?)null)
                    .SetProperty(user => user.HomePublicLatitude, (decimal?)null)
                    .SetProperty(user => user.HomePublicLongitude, (decimal?)null)
                    .SetProperty(user => user.HomeDistrictId, (Guid?)null)
                    .SetProperty(user => user.HomePointUpdatedAt, (DateTime?)null),
                cancellationToken);

    private void Detach(object? entity)
    {
        if (entity is not null)
        {
            _dbContext.Entry(entity).State = EntityState.Detached;
        }
    }

    // A genuine unique-index violation only: SQL Server 2601 (duplicate key on a unique index) and
    // 2627 (on a unique constraint), or SQLite's "UNIQUE constraint failed" (result code 19). The
    // SQLite branch is matched by type name and message because this assembly does not reference
    // Microsoft.Data.Sqlite (only the test host swaps it in). SaveChanges wraps the provider
    // exception in DbUpdateException; ExecuteUpdate lets it through unwrapped, so both shapes are
    // accepted. Anything else (FK violation, timeout, connection loss) keeps propagating.
    internal static bool IsUniqueViolation(Exception exception)
    {
        var inner = exception is DbUpdateException ? exception.InnerException : exception;

        return inner switch
        {
            SqlException { Number: 2601 or 2627 } => true,
            { } other when other.GetType().FullName == "Microsoft.Data.Sqlite.SqliteException"
                           && other.Message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase) => true,
            _ => false
        };
    }
}
