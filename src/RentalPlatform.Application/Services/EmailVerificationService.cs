using System.Security.Cryptography;
using System.Text;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.Common;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Application.Services;

// Email verification (ADR-028). The atomic parts (conditional UPDATEs, transactions, unique
// violations) live in IEmailVerificationStore; this class owns the decisions around them: who may
// be replaced, the cooldown, the per-recipient cap, the global send budget and the ordering
// "write first, send after commit".
public sealed class EmailVerificationService : IEmailVerificationService
{
    public static class ErrorCodes
    {
        public const string DuplicateEmail = "auth.duplicate_email";
        public const string Cooldown = "auth.verification_cooldown";
        public const string RegistrationUnavailable = "auth.registration_unavailable";
        public const string TokenInvalid = "auth.verification_token_invalid";
        public const string TokenExpired = "auth.verification_token_expired";
        public const string AlreadyVerified = "auth.email_already_verified";
        public const string InvalidCredentials = "auth.invalid_credentials";
        public const string UserBlocked = "auth.user_blocked";
    }

    public static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(24);
    public static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan SendCapWindow = TimeSpan.FromHours(24);
    public const int MaxTokensPerWindow = 5;

    private readonly IEmailVerificationStore _store;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IHomePointService _homePointService;
    private readonly IEmailService _emailService;
    private readonly IEmailVerificationSettings _settings;
    private readonly IEmailSendBudget _budget;
    private readonly IEmailVerificationMonitor _monitor;
    private readonly TimeProvider _timeProvider;

    public EmailVerificationService(
        IEmailVerificationStore store,
        IPasswordHasher passwordHasher,
        IHomePointService homePointService,
        IEmailService emailService,
        IEmailVerificationSettings settings,
        IEmailSendBudget budget,
        IEmailVerificationMonitor monitor,
        TimeProvider timeProvider)
    {
        _store = store;
        _passwordHasher = passwordHasher;
        _homePointService = homePointService;
        _emailService = emailService;
        _settings = settings;
        _budget = budget;
        _monitor = monitor;
        _timeProvider = timeProvider;
    }

    public bool IsAvailable => _settings.IsOperational;

    public async Task<ServiceResult<bool>> RegisterPendingAsync(
        User candidate,
        decimal? homeLatitude,
        decimal? homeLongitude,
        CancellationToken cancellationToken = default)
    {
        if (!_settings.IsOperational)
        {
            return Unavailable();
        }

        var now = Now();
        var state = await _store.FindAccountStateAsync(candidate.Email, cancellationToken);
        Guid userId;
        string rawToken;

        if (state is null)
        {
            userId = candidate.Id;
            candidate.CreatedAt = now;
            candidate.IsEmailConfirmed = false;
            candidate.EmailConfirmedAt = null;

            var (raw, token) = NewToken(userId, now);
            rawToken = raw;

            // User + token in ONE SaveChanges: there is never a user without a token to confirm.
            if (!await _store.TryAddUserAsync(candidate, token, cancellationToken))
            {
                return Duplicate();
            }
        }
        else
        {
            // A verified account is never replaced, and neither is a blocked pending one.
            if (state.IsEmailConfirmed || state.IsBlocked)
            {
                return Duplicate();
            }

            userId = state.Id;

            var latest = await _store.GetLatestTokenCreatedAtAsync(userId, TokenPurpose.EmailVerification, cancellationToken);
            if (latest is { } latestCreatedAt && now - latestCreatedAt < Cooldown)
            {
                return CooldownFailure(Cooldown - (now - latestCreatedAt));
            }

            // Resend and re-registration share ONE per-recipient budget (ADR-028 amendment): above it
            // nothing is overwritten. Checked here to skip the work, and again inside the store
            // transaction, where it is race-free.
            if (await _store.CountTokensCreatedSinceAsync(userId, TokenPurpose.EmailVerification, now - SendCapWindow, cancellationToken) >= MaxTokensPerWindow)
            {
                return await CapFailureAsync(userId, now, cancellationToken);
            }

            var (raw, token) = NewToken(userId, now);
            rawToken = raw;

            switch (await _store.TryReplacePendingAsync(userId, candidate, token, now, Limits(now), cancellationToken))
            {
                case ReplacePendingOutcome.Replaced:
                    break;
                case ReplacePendingOutcome.TokenConflict:
                    // A concurrent writer just issued a token: the cooldown, observed a moment late.
                    return CooldownFailure(Cooldown);
                case ReplacePendingOutcome.OverCap:
                    // The recipient hit the 24 h cap (a concurrent writer used the last slot).
                    return await CapFailureAsync(userId, now, cancellationToken);
                default:
                    return Duplicate();
            }
        }

        // The point was validated before anything was written (AuthService); the single writer
        // derives the public pair and district. Result ignored on purpose: the account exists and
        // the user can set the point later, exactly as before verification existed.
        if (homeLatitude is { } latitude && homeLongitude is { } longitude)
        {
            await _homePointService.SetHomePointAsync(userId, latitude, longitude, cancellationToken);

            // This write happens after the commit, so an external sign-in may have reset (and
            // verified) the account in between. If the password hash is no longer ours the point
            // belongs to somebody else: take it back. Single-writer role of HomePointService intact.
            await _store.DiscardHomePointIfAccountChangedAsync(userId, candidate.PasswordHash, cancellationToken);
        }

        await SendAsync(candidate.Email, candidate.PreferredLanguage, rawToken);
        return ServiceResult<bool>.Success(true);
    }

    public async Task<ServiceResult<bool>> ResendAsync(string email, CancellationToken cancellationToken = default)
    {
        if (!_settings.IsOperational)
        {
            return Unavailable();
        }

        var normalized = email.Trim().ToLowerInvariant();
        var state = await _store.FindAccountStateAsync(normalized, cancellationToken);

        // Unknown, verified and blocked accounts all look the same from outside: 202, nothing sent.
        if (state is null || state.IsEmailConfirmed || state.IsBlocked)
        {
            return ServiceResult<bool>.Success(true);
        }

        var now = Now();
        var issued = await _store.CountTokensCreatedSinceAsync(
            state.Id, TokenPurpose.EmailVerification, now - SendCapWindow, cancellationToken);
        if (issued >= MaxTokensPerWindow)
        {
            return ServiceResult<bool>.Success(true);
        }

        var (rawToken, token) = NewToken(state.Id, now);
        var outcome = await _store.TryRotateTokenAsync(token, now, Limits(now), cancellationToken);
        if (outcome != RotateTokenOutcome.Rotated)
        {
            // Inside the cooldown, or a concurrent resend got there first: nothing new to send.
            return ServiceResult<bool>.Success(true);
        }

        await SendAsync(normalized, state.PreferredLanguage, rawToken);
        return ServiceResult<bool>.Success(true);
    }

    public async Task<ServiceResult<Guid>> VerifyAsync(
        string token,
        string password,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return Fail<Guid>(ErrorCodes.TokenInvalid, "The verification link is invalid.");
        }

        var now = Now();
        var view = await _store.FindTokenAsync(HashToken(token), TokenPurpose.EmailVerification, cancellationToken);
        if (view is null)
        {
            return Fail<Guid>(ErrorCodes.TokenInvalid, "The verification link is invalid.");
        }

        if (view.ConsumedAt is not null)
        {
            // Consumed or revoked. On a verified account that is "you are already done"; on an
            // unverified one the link was replaced by a newer registration.
            return view.UserIsEmailConfirmed
                ? Fail<Guid>(ErrorCodes.AlreadyVerified, "This email is already verified.")
                : Fail<Guid>(ErrorCodes.TokenInvalid, "The verification link is invalid.");
        }

        if (view.ExpiresAt <= now)
        {
            return Fail<Guid>(ErrorCodes.TokenExpired, "The verification link has expired.");
        }

        if (view.UserIsBlocked)
        {
            return Fail<Guid>(ErrorCodes.UserBlocked, "User account is blocked.");
        }

        if (view.UserIsEmailConfirmed)
        {
            return Fail<Guid>(ErrorCodes.AlreadyVerified, "This email is already verified.");
        }

        // BCrypt runs here, OUTSIDE the transaction: holding row locks for ~100 ms of hashing would
        // serialise every concurrent verify. The commit re-checks the password hash we saw, so a
        // replacement landing in between cannot leave a verified account with another person's password.
        if (!PasswordPolicy.HasUsablePassword(view.PasswordHash) ||
            !_passwordHasher.VerifyPassword(password, view.PasswordHash))
        {
            return Fail<Guid>(ErrorCodes.InvalidCredentials, "Invalid email or password.");
        }

        var committed = await _store.TryCommitVerificationAsync(
            view.TokenId, view.UserId, TokenPurpose.EmailVerification, view.PasswordHash, now, cancellationToken);
        if (!committed)
        {
            return Fail<Guid>(ErrorCodes.TokenInvalid, "The verification link is invalid.");
        }

        return ServiceResult<Guid>.Success(view.UserId);
    }

    // ---- helpers -----------------------------------------------------------------------------

    private async Task SendAsync(string email, string? preferredLanguage, string rawToken)
    {
        // The global budget counts emails actually sent. Exhausted: the account exists, the user
        // gets 201/202 as usual, and recovers through resend once the window rolls (ADR-028 §8).
        if (!_budget.TryAcquire())
        {
            return;
        }

        var link = _settings.BuildVerificationLink(rawToken);

        // CancellationToken.None: the row is already committed, so a client that disconnected
        // must not also cost the user their email. The sender bounds itself with its own timeout.
        await _emailService.SendEmailVerificationAsync(email, preferredLanguage, link, CancellationToken.None);
    }

    // Retry-After for the daily cap is the REAL wait: until the oldest counted token leaves the
    // window. The client shows a countdown for small values and neutral copy for large ones, so
    // promising 60 s here would be wrong.
    private async Task<ServiceResult<bool>> CapFailureAsync(Guid userId, DateTime now, CancellationToken cancellationToken)
    {
        _monitor.PerRecipientCapReached(userId);

        var oldest = await _store.GetOldestTokenCreatedAtSinceAsync(
            userId, TokenPurpose.EmailVerification, now - SendCapWindow, cancellationToken);

        var remaining = oldest is { } created ? created + SendCapWindow - now : SendCapWindow;
        return CooldownFailure(remaining);
    }

    private static TokenLimits Limits(DateTime now) => new(now - Cooldown, now - SendCapWindow, MaxTokensPerWindow);

    private DateTime Now() => _timeProvider.GetUtcNow().UtcDateTime;

    private static (string Raw, UserToken Token) NewToken(Guid userId, DateTime now)
    {
        var raw = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        return (raw, new UserToken
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Purpose = TokenPurpose.EmailVerification,
            TokenHash = HashToken(raw),
            CreatedAt = now,
            ExpiresAt = now + TokenLifetime
        });
    }

    internal static byte[] HashToken(string rawToken) => SHA256.HashData(Encoding.UTF8.GetBytes(rawToken));

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static ServiceResult<bool> Unavailable() => Fail<bool>(
        ErrorCodes.RegistrationUnavailable, "Registration is temporarily unavailable. Please try again later.");

    private static ServiceResult<bool> Duplicate() => Fail<bool>(
        ErrorCodes.DuplicateEmail, "A user with this email already exists.");

    private static ServiceResult<bool> CooldownFailure(TimeSpan remaining) =>
        ServiceResult<bool>.Failure(new ServiceError
        {
            Code = ErrorCodes.Cooldown,
            Message = "A verification email was sent a moment ago. Please wait before trying again.",
            RetryAfterSeconds = Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds))
        });

    private static ServiceResult<T> Fail<T>(string code, string message) =>
        ServiceResult<T>.Failure(new ServiceError { Code = code, Message = message });
}
