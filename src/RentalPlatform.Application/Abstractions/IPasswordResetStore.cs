namespace RentalPlatform.Application.Abstractions;

public enum IssueResetTokenOutcome
{
    Issued,
    /// <summary>
    /// Nothing was written: the account is no longer verified/unblocked, the recipient is at the cap,
    /// or a token younger than the cooldown exists (also what a concurrent forgot-password produces).
    /// </summary>
    Refused
}

/// <summary>
/// The atomic writes behind password reset (ADR-035 §2). Lock order is Users then UserTokens in every
/// writer, the same as <see cref="IEmailVerificationStore"/>. Reads (account state, token lookup,
/// counts) reuse <see cref="IEmailVerificationStore"/>, which is purpose-aware.
/// </summary>
public interface IPasswordResetStore
{
    /// <summary>
    /// One transaction: lock the user row conditionally on <c>IsEmailConfirmed=1 AND IsBlocked=0</c>,
    /// re-check the per-recipient cap, revoke live reset tokens older than the cooldown, insert
    /// <paramref name="token"/>. A unique-index violation (a fresher live token) rolls back as
    /// <see cref="IssueResetTokenOutcome.Refused"/>.
    /// </summary>
    Task<IssueResetTokenOutcome> TryIssueAsync(
        Domain.Entities.UserToken token, DateTime now, TokenLimits limits, CancellationToken cancellationToken = default);

    /// <summary>
    /// The reset commit, one transaction: <c>UPDATE Users SET PasswordHash=@new WHERE Id AND
    /// IsBlocked=0 AND IsEmailConfirmed=1</c>, then <c>UPDATE UserTokens SET ConsumedAt WHERE Id AND
    /// Purpose=PasswordReset AND ConsumedAt IS NULL AND ExpiresAt&gt;@now</c>. Both must hit exactly one
    /// row or everything rolls back (false). After a commit the change tracker is cleared.
    /// </summary>
    Task<bool> TryCommitResetAsync(
        Guid tokenId, Guid userId, string newPasswordHash, DateTime now, CancellationToken cancellationToken = default);
}
