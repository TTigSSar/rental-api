using System.Security.Cryptography;
using System.Text;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.Common;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Application.Services;

// Password reset by an emailed link (ADR-035). The atomic parts live in IPasswordResetStore; reads
// reuse IEmailVerificationStore (purpose-aware). This class owns the decisions around them: who may
// receive a link, the cooldown, the per-recipient cap, the shared global send budget and the ordering
// "write first, send after commit". Everything the outside can observe on forgot-password is a 202,
// whatever happened (anti-enumeration, ADR-035 section 6), except the two account-independent 503s:
// email not configured, and the exhausted global send budget (section 7 amendment).
public sealed class PasswordResetService : IPasswordResetService
{
    public static class ErrorCodes
    {
        public const string TokenInvalid = "auth.reset_token_invalid";
        public const string TokenExpired = "auth.reset_token_expired";
        public const string Unavailable = "auth.password_reset_unavailable";
        public const string PasswordTooLong = "auth.password_too_long";
    }

    public static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan SendCapWindow = TimeSpan.FromHours(24);
    public const int MaxTokensPerWindow = 5;

    private static readonly HashSet<string> SupportedLanguages = new(StringComparer.Ordinal) { "en", "hy", "ru" };

    private readonly IEmailVerificationStore _reads;
    private readonly IPasswordResetStore _store;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IEmailService _emailService;
    private readonly IEmailVerificationSettings _settings;
    private readonly IEmailSendBudget _budget;
    private readonly IEmailVerificationMonitor _monitor;
    private readonly TimeProvider _timeProvider;

    public PasswordResetService(
        IEmailVerificationStore reads,
        IPasswordResetStore store,
        IPasswordHasher passwordHasher,
        IEmailService emailService,
        IEmailVerificationSettings settings,
        IEmailSendBudget budget,
        IEmailVerificationMonitor monitor,
        TimeProvider timeProvider)
    {
        _monitor = monitor;
        _reads = reads;
        _store = store;
        _passwordHasher = passwordHasher;
        _emailService = emailService;
        _settings = settings;
        _budget = budget;
        _timeProvider = timeProvider;
    }

    public async Task<ServiceResult<bool>> ForgotAsync(
        string? email, string? requestLanguage, CancellationToken cancellationToken = default)
    {
        // Same gate as register/resend (ADR-028 section 9, ADR-035 section 10): a 202 would promise a
        // link that cannot be sent. The 503 says nothing about the account, so section 6 holds.
        if (!_settings.IsOperational)
        {
            return Fail<bool>(ErrorCodes.Unavailable, "Password reset is temporarily unavailable. Please try again later.");
        }

        // Exhausted shared budget (ADR-035 section 7 amendment): the same 503, checked before any account
        // lookup and for every request, so unknown and existing emails stay indistinguishable. Nothing is
        // reserved here; the slot is taken inside the issue transaction.
        if (!_budget.HasCapacity())
        {
            return Fail<bool>(ErrorCodes.Unavailable, "Password reset is temporarily unavailable. Please try again later.");
        }

        var normalized = (email ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized.Length == 0)
        {
            return ServiceResult<bool>.Success(true);
        }

        var state = await _reads.FindAccountStateAsync(normalized, cancellationToken);

        // Unknown, blocked and unverified (pending) accounts all look the same from outside: nothing sent.
        // A verified external-only account (empty hash) is allowed on purpose (ADR-035 section 5).
        if (state is null || state.IsBlocked || !state.IsEmailConfirmed)
        {
            return ServiceResult<bool>.Success(true);
        }

        var now = Now();

        var latest = await _reads.GetLatestTokenCreatedAtAsync(state.Id, TokenPurpose.PasswordReset, cancellationToken);
        if (latest is { } latestCreatedAt && now - latestCreatedAt < Cooldown)
        {
            return ServiceResult<bool>.Success(true);
        }

        // Counted over reset tokens only: verification squatting must not lock the owner out (section 7).
        if (await _reads.CountTokensCreatedSinceAsync(state.Id, TokenPurpose.PasswordReset, now - SendCapWindow, cancellationToken) >= MaxTokensPerWindow)
        {
            // Stays a silent 202 (section 7): an answer here would reveal the account. User id only.
            _monitor.PasswordResetCapReached(state.Id);
            return ServiceResult<bool>.Success(true);
        }

        var (rawToken, token) = NewToken(state.Id, now);
        var limits = new TokenLimits(now - Cooldown, now - SendCapWindow, MaxTokensPerWindow);

        // The global budget is shared with verification email (one provider quota). The slot is reserved
        // by the store inside the issue transaction, after the insert succeeded: a refused issue never
        // costs budget, and if the budget raced to zero since the check above everything rolls back.
        var outcome = await _store.TryIssueAsync(token, now, limits, _budget.TryAcquire, cancellationToken);
        if (outcome == IssueResetTokenOutcome.BudgetExhausted)
        {
            return Fail<bool>(ErrorCodes.Unavailable, "Password reset is temporarily unavailable. Please try again later.");
        }

        if (outcome != IssueResetTokenOutcome.Issued)
        {
            // Cooldown seen late, cap reached by a concurrent writer, or the account changed state.
            _monitor.PasswordResetRefused(state.Id);
            return ServiceResult<bool>.Success(true);
        }

        var language = ResolveLanguage(requestLanguage) ?? ResolveLanguage(state.PreferredLanguage);
        var link = _settings.BuildPasswordResetLink(rawToken);

        // CancellationToken.None: the row is committed, so a client that disconnected must not also
        // cost the user their email. The sender bounds itself with its own timeout.
        await _emailService.SendPasswordResetAsync(normalized, language, link, CancellationToken.None);
        return ServiceResult<bool>.Success(true);
    }

    public async Task<ServiceResult<bool>> CheckAsync(string token, CancellationToken cancellationToken = default)
    {
        var lookup = await LookupLiveTokenAsync(token, cancellationToken);
        return lookup.Error is { } error
            ? ServiceResult<bool>.Failure(error)
            : ServiceResult<bool>.Success(true);
    }

    public async Task<ServiceResult<Guid>> ResetAsync(
        string token, string newPassword, CancellationToken cancellationToken = default)
    {
        // Byte length, not char length: BCrypt truncates at 72 UTF-8 bytes (see PasswordPolicy).
        if (Encoding.UTF8.GetByteCount(newPassword) > PasswordPolicy.MaxPasswordBytes)
        {
            return Fail<Guid>(ErrorCodes.PasswordTooLong, $"Password must be at most {PasswordPolicy.MaxPasswordBytes} bytes (UTF-8).");
        }

        var lookup = await LookupLiveTokenAsync(token, cancellationToken);
        if (lookup.Error is { } error)
        {
            return ServiceResult<Guid>.Failure(error);
        }

        var view = lookup.View!;

        // BCrypt runs OUTSIDE the transaction: holding row locks for ~100 ms of hashing would
        // serialise every concurrent writer on this account.
        var newHash = _passwordHasher.HashPassword(newPassword);

        var committed = await _store.TryCommitResetAsync(view.TokenId, view.UserId, newHash, Now(), cancellationToken);
        if (!committed)
        {
            // Lost a race: consumed or revoked by a concurrent request, or the account was blocked.
            return Fail<Guid>(ErrorCodes.TokenInvalid, "The password reset link is invalid.");
        }

        return ServiceResult<Guid>.Success(view.UserId);
    }

    // ---- helpers -----------------------------------------------------------------------------

    // Shared by check and reset so the two can never disagree about what "live" means.
    private async Task<(VerificationTokenView? View, ServiceError? Error)> LookupLiveTokenAsync(
        string token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return (null, Error(ErrorCodes.TokenInvalid, "The password reset link is invalid."));
        }

        var view = await _reads.FindTokenAsync(HashToken(token), TokenPurpose.PasswordReset, cancellationToken);
        if (view is null || view.ConsumedAt is not null)
        {
            return (null, Error(ErrorCodes.TokenInvalid, "The password reset link is invalid."));
        }

        if (view.ExpiresAt <= Now())
        {
            return (null, Error(ErrorCodes.TokenExpired, "The password reset link has expired."));
        }

        // A blocked or unverified owner never gets a usable link (ADR-035 section 4).
        if (view.UserIsBlocked || !view.UserIsEmailConfirmed)
        {
            return (null, Error(ErrorCodes.TokenInvalid, "The password reset link is invalid."));
        }

        return (view, null);
    }

    // The request language wins only when it names a supported one (hy-AM / ru_RU count); otherwise
    // the stored preference; otherwise null, which the template renders as English (ADR-035 section 9).
    private static string? ResolveLanguage(string? language)
    {
        var code = (language ?? string.Empty).Trim().ToLowerInvariant();
        var separator = code.IndexOfAny(new[] { '-', '_' });
        if (separator > 0)
        {
            code = code[..separator];
        }

        return SupportedLanguages.Contains(code) ? code : null;
    }

    private DateTime Now() => _timeProvider.GetUtcNow().UtcDateTime;

    private static (string Raw, UserToken Token) NewToken(Guid userId, DateTime now)
    {
        var raw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return (raw, new UserToken
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Purpose = TokenPurpose.PasswordReset,
            TokenHash = HashToken(raw),
            CreatedAt = now,
            ExpiresAt = now + TokenLifetime
        });
    }

    private static byte[] HashToken(string rawToken) => SHA256.HashData(Encoding.UTF8.GetBytes(rawToken));

    private static ServiceError Error(string code, string message) => new() { Code = code, Message = message };

    private static ServiceResult<T> Fail<T>(string code, string message) =>
        ServiceResult<T>.Failure(Error(code, message));
}
