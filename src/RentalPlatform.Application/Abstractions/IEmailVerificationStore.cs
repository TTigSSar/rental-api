using RentalPlatform.Application.DTOs;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Application.Abstractions;

/// <summary>Untracked snapshot of an account, enough to decide register/resend/external outcomes.</summary>
public sealed record AccountState(Guid Id, bool IsEmailConfirmed, bool IsBlocked, string? PreferredLanguage);

/// <summary>Untracked snapshot of a token and its owner, as read by verify-email.</summary>
public sealed record VerificationTokenView(
    Guid TokenId,
    Guid UserId,
    DateTime ExpiresAt,
    DateTime? ConsumedAt,
    bool UserIsEmailConfirmed,
    bool UserIsBlocked,
    string PasswordHash);

public enum ReplacePendingOutcome
{
    Replaced,
    /// <summary>0 rows matched: the account was verified or blocked in the meantime.</summary>
    NotReplaceable,
    /// <summary>A concurrent writer holds a fresh active token (cooldown semantics).</summary>
    TokenConflict
}

public enum RotateTokenOutcome
{
    Rotated,
    /// <summary>An active token younger than the cooldown exists, or a concurrent resend won.</summary>
    Conflict
}

/// <summary>
/// Every conditional UPDATE, transaction and unique-violation translation behind email
/// verification (ADR-028 §4). Writers take locks in the order Users then UserTokens. Implemented in
/// Infrastructure because the atomicity lives in SQL, never in an Application service.
/// </summary>
public interface IEmailVerificationStore
{
    Task<AccountState?> FindAccountStateAsync(string email, CancellationToken cancellationToken = default);

    Task<DateTime?> GetLatestTokenCreatedAtAsync(Guid userId, TokenPurpose purpose, CancellationToken cancellationToken = default);

    Task<int> CountTokensCreatedSinceAsync(Guid userId, TokenPurpose purpose, DateTime since, CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts the user (and the optional first token) in ONE SaveChanges. False on a unique
    /// violation (the email, or any concurrent duplicate); the entities are detached again.
    /// </summary>
    Task<bool> TryAddUserAsync(User user, UserToken? token, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces a pending registration in one transaction: conditional UPDATE of the user
    /// (<c>IsEmailConfirmed=0 AND IsBlocked=0</c>) with the candidate's password, profile and a reset
    /// CreatedAt and Home*, then revokes the active tokens, then inserts <paramref name="token"/>.
    /// </summary>
    Task<ReplacePendingOutcome> TryReplacePendingAsync(
        Guid userId, User candidate, UserToken token, DateTime now, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resend: revokes active tokens created before <paramref name="cooldownCutoff"/>, then inserts
    /// <paramref name="token"/>. A unique violation (a younger active token) is <see cref="RotateTokenOutcome.Conflict"/>.
    /// </summary>
    Task<RotateTokenOutcome> TryRotateTokenAsync(
        UserToken token, DateTime cooldownCutoff, DateTime now, CancellationToken cancellationToken = default);

    Task<VerificationTokenView?> FindTokenAsync(byte[] tokenHash, TokenPurpose purpose, CancellationToken cancellationToken = default);

    /// <summary>
    /// The verify commit: in one transaction, <c>UPDATE Users … WHERE Id AND IsEmailConfirmed=0 AND
    /// IsBlocked=0 AND PasswordHash=@seenHash</c>, then <c>UPDATE UserTokens … WHERE Id AND Purpose AND
    /// ConsumedAt IS NULL AND ExpiresAt&gt;@now</c>. Both must hit exactly one row or it rolls back
    /// and returns false. After a commit the change tracker is cleared.
    /// </summary>
    Task<bool> TryCommitVerificationAsync(
        Guid tokenId, Guid userId, TokenPurpose purpose, string seenPasswordHash, DateTime now,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// A Google/Apple sign-in on a pending email: conditional reset (PasswordHash="", provider
    /// profile and ids, verified) and revoke of the active tokens, in one transaction. False when
    /// the account is no longer pending and unblocked.
    /// </summary>
    Task<bool> TryResetPendingForExternalAsync(
        Guid userId, ExternalUserInfo external, string firstName, string lastName, DateTime now,
        CancellationToken cancellationToken = default);
}
