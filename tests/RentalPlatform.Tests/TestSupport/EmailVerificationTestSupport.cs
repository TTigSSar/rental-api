using System.Text.RegularExpressions;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.DTOs;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Tests.TestSupport;

// A clock the test moves by hand. (The Microsoft.Extensions.TimeProvider.Testing package is not a
// dependency of this project; a TimeProvider only needs GetUtcNow overridden.)
public sealed class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _now;

    public FakeTimeProvider(DateTimeOffset? start = null)
    {
        _now = start ?? new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
    }

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;

    public void Set(DateTimeOffset value) => _now = value;
}

// Settings double: the gate is open unless a test closes it.
public sealed class FakeEmailVerificationSettings : IEmailVerificationSettings
{
    public bool IsOperational { get; set; } = true;

    public string BuildVerificationLink(string token) => $"https://test.local/auth/verify-email#token={token}";
}

// Budget double: unlimited unless a test sets Remaining.
public sealed class FakeEmailSendBudget : IEmailSendBudget
{
    public int? Remaining { get; set; }

    public int Acquired { get; private set; }

    public bool TryAcquire()
    {
        if (Remaining is { } remaining)
        {
            if (remaining <= 0)
            {
                return false;
            }

            Remaining = remaining - 1;
        }

        Acquired++;
        return true;
    }
}

// Transport double that records what would have gone out, so a test can read the link.
public sealed class CaptureEmailSender : IEmailSender
{
    private static readonly Regex TokenPattern = new(@"#token=([A-Za-z0-9_\-]+)", RegexOptions.Compiled);

    private readonly object _gate = new();
    private readonly List<EmailMessage> _messages = new();

    public IReadOnlyList<EmailMessage> Messages
    {
        get
        {
            lock (_gate)
            {
                return _messages.ToArray();
            }
        }
    }

    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _messages.Add(message);
        }

        return Task.CompletedTask;
    }

    public void Clear()
    {
        lock (_gate)
        {
            _messages.Clear();
        }
    }

    public static string? ExtractToken(string body)
    {
        var match = TokenPattern.Match(body);
        return match.Success ? match.Groups[1].Value : null;
    }

    public string? LastTokenFor(string email) =>
        Messages.LastOrDefault(message => string.Equals(message.To, email, StringComparison.OrdinalIgnoreCase)) is { } last
            ? ExtractToken(last.TextBody)
            : null;

    public int CountFor(string email) =>
        Messages.Count(message => string.Equals(message.To, email, StringComparison.OrdinalIgnoreCase));
}

// Minimal IEmailVerificationStore over FakeUserAuthStore for the unit tests that only need
// "register creates a user" without a database. Behaviour that is really SQL (replacement, verify
// commit, resend rotation) is covered against the real store in EmailVerification*Tests.
public sealed class FakeEmailVerificationStore : IEmailVerificationStore
{
    private readonly FakeUserAuthStore _users;

    public FakeEmailVerificationStore(FakeUserAuthStore users) => _users = users;

    public List<UserToken> Tokens { get; } = new();

    public Task<AccountState?> FindAccountStateAsync(string email, CancellationToken cancellationToken = default)
    {
        var user = _users.Users.FirstOrDefault(candidate => candidate.Email == email);
        return Task.FromResult(user is null
            ? null
            : new AccountState(user.Id, user.IsEmailConfirmed, user.IsBlocked, user.PreferredLanguage));
    }

    public Task<bool> TryAddUserAsync(User user, UserToken? token, CancellationToken cancellationToken = default)
    {
        if (_users.Users.Any(existing => existing.Email == user.Email))
        {
            return Task.FromResult(false);
        }

        _users.Seed(user);
        if (token is not null)
        {
            Tokens.Add(token);
        }

        return Task.FromResult(true);
    }

    public Task<DateTime?> GetLatestTokenCreatedAtAsync(Guid userId, TokenPurpose purpose, CancellationToken cancellationToken = default) =>
        Task.FromResult(Tokens.Where(token => token.UserId == userId).Select(token => (DateTime?)token.CreatedAt).Max());

    public Task<int> CountTokensCreatedSinceAsync(Guid userId, TokenPurpose purpose, DateTime since, CancellationToken cancellationToken = default) =>
        Task.FromResult(Tokens.Count(token => token.UserId == userId && token.CreatedAt >= since));

    public Task<ReplacePendingOutcome> TryReplacePendingAsync(Guid userId, User candidate, UserToken token, DateTime now, TokenLimits limits, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Use the real EmailVerificationStore over SQLite.");

    public Task<RotateTokenOutcome> TryRotateTokenAsync(UserToken token, DateTime now, TokenLimits limits, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Use the real EmailVerificationStore over SQLite.");

    public Task<VerificationTokenView?> FindTokenAsync(byte[] tokenHash, TokenPurpose purpose, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Use the real EmailVerificationStore over SQLite.");

    public Task<bool> TryCommitVerificationAsync(Guid tokenId, Guid userId, TokenPurpose purpose, string seenPasswordHash, DateTime now, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Use the real EmailVerificationStore over SQLite.");

    public Task<int> DiscardHomePointIfAccountChangedAsync(Guid userId, string registrantPasswordHash, CancellationToken cancellationToken = default) =>
        Task.FromResult(0);

    public Task<bool> TryResetPendingForExternalAsync(Guid userId, ExternalUserInfo external, string firstName, string lastName, DateTime now, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Use the real EmailVerificationStore over SQLite.");
}

// Wires AuthService + EmailVerificationService over the in-memory fakes. Existing unit tests that
// predate email verification use this so their constructor lines stay one-liners.
public static class AuthServiceFactory
{
    public static RentalPlatform.Application.Services.AuthService ForFakes(
        FakeUserAuthStore store,
        Guid? currentUserId,
        IPasswordHasher? passwordHasher = null,
        IHomePointService? homePointService = null,
        TimeProvider? timeProvider = null)
    {
        passwordHasher ??= new FakePasswordHasher();
        homePointService ??= new FakeHomePointService(store);
        timeProvider ??= new FakeTimeProvider();

        var verification = new RentalPlatform.Application.Services.EmailVerificationService(
            new FakeEmailVerificationStore(store),
            passwordHasher,
            homePointService,
            new FakeEmailService(),
            new FakeEmailVerificationSettings(),
            new FakeEmailSendBudget(),
            timeProvider);

        return new RentalPlatform.Application.Services.AuthService(
            store,
            passwordHasher,
            new FakeJwtTokenService(),
            new FakeCurrentUserContext(currentUserId),
            new FakeExternalIdentityTokenValidator(),
            homePointService,
            verification,
            new FakeEmailVerificationStore(store),
            timeProvider);
    }
}

// External validator whose answer a test chooses.
public sealed class StubExternalIdentityTokenValidator : IExternalIdentityTokenValidator
{
    public ExternalUserInfo? Result { get; set; }

    public Task<RentalPlatform.Application.Common.ServiceResult<ExternalUserInfo>> ValidateAsync(
        string provider, string idToken, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result is null
            ? RentalPlatform.Application.Common.ServiceResult<ExternalUserInfo>.Failure(
                new RentalPlatform.Application.Common.ServiceError { Code = "auth.external_invalid_token", Message = "stub" })
            : RentalPlatform.Application.Common.ServiceResult<ExternalUserInfo>.Success(Result));
}

// The real stack the verification feature runs on - real stores, real EmailService and templates,
// real AuthService/EmailVerificationService - over ONE DbContext (like a request scope), with the
// transport, the clock, the settings gate and the budget as the only doubles. Each harness owns its
// own DbContext, so concurrency tests build several over the same database.
public sealed class AuthHarness
{
    public AuthHarness(
        RentalPlatform.Infrastructure.Persistence.AppDbContext context,
        IPasswordHasher? passwordHasher = null,
        FakeTimeProvider? clock = null,
        CaptureEmailSender? sender = null,
        Func<IEmailVerificationStore, IEmailVerificationStore>? decorateStore = null,
        Func<IUserAuthStore, IUserAuthStore>? decorateUserStore = null)
    {
        Context = context;
        Clock = clock ?? new FakeTimeProvider();
        Sender = sender ?? new CaptureEmailSender();
        PasswordHasher = passwordHasher ?? new FakePasswordHasher();

        var userStore = new RentalPlatform.Infrastructure.Persistence.UserAuthStore(context);
        UserStore = userStore;
        Store = new RentalPlatform.Infrastructure.Persistence.EmailVerificationStore(context);
        IEmailVerificationStore verificationStore = decorateStore is null ? Store : decorateStore(Store);
        IUserAuthStore authUserStore = decorateUserStore is null ? userStore : decorateUserStore(userStore);

        var homePoints = new RentalPlatform.Application.Services.HomePointService(
            new RentalPlatform.Infrastructure.Persistence.HomePointStore(context),
            new RentalPlatform.Infrastructure.Services.GeohashSnapper(),
            new RentalPlatform.Infrastructure.Services.DistrictBoundaryProvider(),
            new FakeNotificationEmitter());

        var emailService = new RentalPlatform.Infrastructure.Services.EmailService(
            Sender,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RentalPlatform.Infrastructure.Services.EmailService>.Instance);

        Verification = new RentalPlatform.Application.Services.EmailVerificationService(
            verificationStore, PasswordHasher, homePoints, emailService, Settings, Budget, Clock);

        Auth = new RentalPlatform.Application.Services.AuthService(
            authUserStore,
            PasswordHasher,
            new FakeJwtTokenService(),
            new FakeCurrentUserContext(null),
            External,
            homePoints,
            Verification,
            verificationStore,
            Clock);
    }

    public RentalPlatform.Infrastructure.Persistence.AppDbContext Context { get; }
    public FakeTimeProvider Clock { get; }
    public CaptureEmailSender Sender { get; }
    public IPasswordHasher PasswordHasher { get; }
    public FakeEmailVerificationSettings Settings { get; } = new();
    public FakeEmailSendBudget Budget { get; } = new();
    public StubExternalIdentityTokenValidator External { get; } = new();
    public RentalPlatform.Infrastructure.Persistence.UserAuthStore UserStore { get; }
    public RentalPlatform.Infrastructure.Persistence.EmailVerificationStore Store { get; }
    public RentalPlatform.Application.Services.EmailVerificationService Verification { get; }
    public RentalPlatform.Application.Services.AuthService Auth { get; }

    public static RegisterRequest Register(
        string email = "new.user@test.local",
        string password = "Sufficient1Password",
        string firstName = "New",
        string lastName = "User",
        string phone = "+374 99 123456",
        string? language = "en",
        decimal? latitude = null,
        decimal? longitude = null) => new()
    {
        Email = email,
        Password = password,
        FirstName = firstName,
        LastName = lastName,
        PhoneNumber = phone,
        PreferredLanguage = language,
        HomeLatitude = latitude,
        HomeLongitude = longitude
    };
}
