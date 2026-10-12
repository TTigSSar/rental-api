using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.DTOs;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Tests.TestSupport;

public sealed class FakeHostEnvironment : IHostEnvironment
{
    public FakeHostEnvironment(string environmentName) => EnvironmentName = environmentName;

    public string EnvironmentName { get; set; }
    public string ApplicationName { get; set; } = "RentalPlatform.Tests";
    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

// Collects every log entry (level, rendered message, exception) so a test can assert on what was,
// and what must NOT have been, written - the link token above all (M-013).
public sealed class ListLogger<T> : ILogger<T>
{
    public sealed record Entry(LogLevel Level, string Message, Exception? Exception);

    private readonly List<Entry> _entries = new();

    public IReadOnlyList<Entry> Entries
    {
        get
        {
            lock (_entries)
            {
                return _entries.ToArray();
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_entries)
        {
            _entries.Add(new Entry(logLevel, formatter(state, exception), exception));
        }
    }

    // Everything a log line could leak: the message and the full exception text.
    public string AllText() =>
        string.Join("\n", Entries.Select(entry => entry.Message + "\n" + entry.Exception));
}

// Runs an action right after a successful password check - the exact window between BCrypt and the
// verify transaction in which a replacement can land.
public sealed class HookedPasswordHasher : IPasswordHasher
{
    private readonly IPasswordHasher _inner;
    private Action? _afterVerify;

    public HookedPasswordHasher(IPasswordHasher inner) => _inner = inner;

    public void RunOnceAfterVerify(Action action) => _afterVerify = action;

    public string HashPassword(string password) => _inner.HashPassword(password);

    public bool VerifyPassword(string password, string passwordHash)
    {
        var ok = _inner.VerifyPassword(password, passwordHash);
        var hook = _afterVerify;
        _afterVerify = null;
        hook?.Invoke();
        return ok;
    }
}

public sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;

    public StubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) =>
        _respond = respond;

    public List<(HttpRequestMessage Request, string Body)> Calls { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        Calls.Add((request, body));
        return await _respond(request, cancellationToken);
    }
}

public sealed class StubHttpClientFactory : IHttpClientFactory
{
    private readonly HttpMessageHandler _handler;

    public StubHttpClientFactory(HttpMessageHandler handler) => _handler = handler;

    public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
}

// Delegates to the real store but lets a test hold every caller at "I have read the account state"
// until all of them have, so the writes that follow genuinely overlap.
public sealed class BarrierEmailVerificationStore : IEmailVerificationStore
{
    private readonly IEmailVerificationStore _inner;
    private readonly AsyncBarrier _barrier;

    public BarrierEmailVerificationStore(IEmailVerificationStore inner, AsyncBarrier barrier)
    {
        _inner = inner;
        _barrier = barrier;
    }

    public async Task<AccountState?> FindAccountStateAsync(string email, CancellationToken cancellationToken = default)
    {
        var state = await _inner.FindAccountStateAsync(email, cancellationToken);
        await _barrier.SignalAndWaitAsync();
        return state;
    }

    public Task<DateTime?> GetLatestTokenCreatedAtAsync(Guid userId, TokenPurpose purpose, CancellationToken cancellationToken = default) =>
        _inner.GetLatestTokenCreatedAtAsync(userId, purpose, cancellationToken);

    public Task<DateTime?> GetOldestTokenCreatedAtSinceAsync(Guid userId, TokenPurpose purpose, DateTime since, CancellationToken cancellationToken = default) =>
        _inner.GetOldestTokenCreatedAtSinceAsync(userId, purpose, since, cancellationToken);

    public Task<int> CountTokensCreatedSinceAsync(Guid userId, TokenPurpose purpose, DateTime since, CancellationToken cancellationToken = default) =>
        _inner.CountTokensCreatedSinceAsync(userId, purpose, since, cancellationToken);

    public Task<bool> TryAddUserAsync(User user, UserToken? token, CancellationToken cancellationToken = default) =>
        _inner.TryAddUserAsync(user, token, cancellationToken);

    public Task<ReplacePendingOutcome> TryReplacePendingAsync(Guid userId, User candidate, UserToken token, DateTime now, TokenLimits limits, CancellationToken cancellationToken = default) =>
        _inner.TryReplacePendingAsync(userId, candidate, token, now, limits, cancellationToken);

    public Task<RotateTokenOutcome> TryRotateTokenAsync(UserToken token, DateTime now, TokenLimits limits, CancellationToken cancellationToken = default) =>
        _inner.TryRotateTokenAsync(token, now, limits, cancellationToken);

    public Task<VerificationTokenView?> FindTokenAsync(byte[] tokenHash, TokenPurpose purpose, CancellationToken cancellationToken = default) =>
        _inner.FindTokenAsync(tokenHash, purpose, cancellationToken);

    public Task<bool> TryCommitVerificationAsync(Guid tokenId, Guid userId, TokenPurpose purpose, string seenPasswordHash, DateTime now, CancellationToken cancellationToken = default) =>
        _inner.TryCommitVerificationAsync(tokenId, userId, purpose, seenPasswordHash, now, cancellationToken);

    public Task<int> DiscardHomePointIfAccountChangedAsync(Guid userId, string registrantPasswordHash, CancellationToken cancellationToken = default) =>
        _inner.DiscardHomePointIfAccountChangedAsync(userId, registrantPasswordHash, cancellationToken);

    public Task<bool> TryResetPendingForExternalAsync(Guid userId, ExternalUserInfo external, string firstName, string lastName, string? preferredLanguage, DateTime now, CancellationToken cancellationToken = default) =>
        _inner.TryResetPendingForExternalAsync(userId, external, firstName, lastName, preferredLanguage, now, cancellationToken);
}

public sealed class AsyncBarrier
{
    private readonly int _participants;
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _arrived;

    public AsyncBarrier(int participants) => _participants = participants;

    public Task SignalAndWaitAsync()
    {
        if (Interlocked.Increment(ref _arrived) >= _participants)
        {
            _released.TrySetResult();
        }

        return _released.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}

// Pretends the email was free at lookup time and inserts a rival verified account right after, so
// ExternalAsync's insert hits the unique index on Users.Email - the race the retry loop exists for.
public sealed class RacingUserAuthStore : IUserAuthStore
{
    private readonly IUserAuthStore _inner;
    private readonly Func<Task> _insertRival;
    private bool _raced;

    public RacingUserAuthStore(IUserAuthStore inner, Func<Task> insertRival)
    {
        _inner = inner;
        _insertRival = insertRival;
    }

    public Task<bool> EmailExistsAsync(string email, CancellationToken cancellationToken = default) =>
        _inner.EmailExistsAsync(email, cancellationToken);

    public async Task<User?> FindByEmailAsync(string email, CancellationToken cancellationToken = default)
    {
        if (!_raced)
        {
            _raced = true;
            await _insertRival();
            return null; // the stale answer: "no such user yet"
        }

        return await _inner.FindByEmailAsync(email, cancellationToken);
    }

    public Task<User?> FindByExternalProviderAsync(string provider, string externalProviderId, CancellationToken cancellationToken = default) =>
        _inner.FindByExternalProviderAsync(provider, externalProviderId, cancellationToken);

    public Task<User?> FindByIdAsync(Guid userId, CancellationToken cancellationToken = default) =>
        _inner.FindByIdAsync(userId, cancellationToken);

    public Task AddAsync(User user, CancellationToken cancellationToken = default) =>
        _inner.AddAsync(user, cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
        _inner.SaveChangesAsync(cancellationToken);

    public Task<bool> TrySaveChangesAsync(CancellationToken cancellationToken = default) =>
        _inner.TrySaveChangesAsync(cancellationToken);
}
