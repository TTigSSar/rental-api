using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Infrastructure.Persistence;
using RentalPlatform.Infrastructure.Services;

namespace RentalPlatform.Tests.TestSupport;

// Shared pieces of the ADR-055 session-revocation tests.
public static class SessionRevocationTestSupport
{
    // The real token service, configured like the test host.
    public static JwtTokenService RealTokenService() => new(Options.Create(new JwtOptions
    {
        Issuer = "RentalPlatform",
        Audience = "RentalPlatform.Api",
        SecretKey = "xunit-integration-test-signing-key-abc123",
        AccessTokenExpirationMinutes = 60
    }));

    // A real issued token for a user at the given session version.
    public static string TokenFor(Guid userId, int tokenVersion)
    {
        var user = TestData.User(userId, $"{userId:N}@token.local");
        user.TokenVersion = tokenVersion;
        return RealTokenService().GenerateAccessToken(user);
    }

    // The claims of a real issued token, as the JWT bearer handler would hand them to the check.
    public static ClaimsPrincipal PrincipalOf(string accessToken) =>
        new(new ClaimsIdentity(new JwtSecurityTokenHandler().ReadJwtToken(accessToken).Claims, "test"));

    public static int TokenVersionClaim(string accessToken) =>
        int.Parse(new JwtSecurityTokenHandler().ReadJwtToken(accessToken).Claims.Single(claim => claim.Type == "tv").Value);

    public static UserSecurityStateCache NewCache(IUserSecurityStateReader reader) =>
        new(new Microsoft.Extensions.Caching.Memory.MemoryCache(
            new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()), reader);

    // The production reader's query over a test database.
    public sealed class DbReader : IUserSecurityStateReader
    {
        private readonly Func<AppDbContext> _createContext;

        public DbReader(Func<AppDbContext> createContext) => _createContext = createContext;

        public async Task<UserSecurityState?> ReadAsync(Guid userId, CancellationToken cancellationToken)
        {
            await using var context = _createContext();
            var row = await context.Users.AsNoTracking()
                .Where(user => user.Id == userId)
                .Select(user => new { user.TokenVersion })
                .FirstOrDefaultAsync(cancellationToken);
            return row is null ? null : new UserSecurityState(row.TokenVersion);
        }
    }

    // Pauses the FIRST read after its database read has completed, i.e. between the DB read and the cache Set.
    public sealed class GatedReader : IUserSecurityStateReader
    {
        private readonly IUserSecurityStateReader _inner;
        private readonly TaskCompletionSource _readCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public GatedReader(IUserSecurityStateReader inner) => _inner = inner;

        public Task ReadCompleted => _readCompleted.Task.WaitAsync(TimeSpan.FromSeconds(30));
        public void Release() => _release.TrySetResult();

        public async Task<UserSecurityState?> ReadAsync(Guid userId, CancellationToken cancellationToken)
        {
            var state = await _inner.ReadAsync(userId, cancellationToken);
            if (Interlocked.Increment(ref _calls) == 1)
            {
                _readCompleted.TrySetResult();
                await _release.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            }

            return state;
        }
    }

    public sealed class ThrowingReader : IUserSecurityStateReader
    {
        public Task<UserSecurityState?> ReadAsync(Guid userId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("simulated database failure");
    }

    // Runs a callback right after a lookup, i.e. between a service's read and its write.
    public sealed class HookUserAuthStore : IUserAuthStore
    {
        private readonly IUserAuthStore _inner;
        private readonly Func<Task>? _afterFindByEmail;
        private readonly Func<Task>? _afterFindById;
        private bool _emailHookRan;
        private bool _idHookRan;

        public HookUserAuthStore(IUserAuthStore inner, Func<Task>? afterFindByEmail = null, Func<Task>? afterFindById = null)
        {
            _inner = inner;
            _afterFindByEmail = afterFindByEmail;
            _afterFindById = afterFindById;
        }

        public async Task<User?> FindByEmailAsync(string email, CancellationToken cancellationToken = default)
        {
            var user = await _inner.FindByEmailAsync(email, cancellationToken);
            if (_afterFindByEmail is not null && !_emailHookRan)
            {
                _emailHookRan = true;
                await _afterFindByEmail();
            }

            return user;
        }

        public async Task<User?> FindByIdAsync(Guid userId, CancellationToken cancellationToken = default)
        {
            var user = await _inner.FindByIdAsync(userId, cancellationToken);
            if (_afterFindById is not null && !_idHookRan)
            {
                _idHookRan = true;
                await _afterFindById();
            }

            return user;
        }

        public Task<bool> EmailExistsAsync(string email, CancellationToken cancellationToken = default) => _inner.EmailExistsAsync(email, cancellationToken);
        public Task<User?> FindByExternalProviderAsync(string provider, string externalProviderId, CancellationToken cancellationToken = default) => _inner.FindByExternalProviderAsync(provider, externalProviderId, cancellationToken);
        public Task AddAsync(User user, CancellationToken cancellationToken = default) => _inner.AddAsync(user, cancellationToken);
        public Task SaveChangesAsync(CancellationToken cancellationToken = default) => _inner.SaveChangesAsync(cancellationToken);
        public Task<bool> TrySaveChangesAsync(CancellationToken cancellationToken = default) => _inner.TrySaveChangesAsync(cancellationToken);
        public Task<bool> TryChangePasswordAsync(User user, string newPasswordHash, CancellationToken cancellationToken = default) => _inner.TryChangePasswordAsync(user, newPasswordHash, cancellationToken);
    }

    // A hub connection stand-in. Abort() counts, or throws when asked to; the user identifier is
    // unparseable when asked to.
    public sealed class FakeHubCallerContext : HubCallerContext
    {
        private readonly ClaimsPrincipal _user;

        public FakeHubCallerContext(
            Guid userId, string connectionId, string? accessToken = null, bool throwOnAbort = false, string? userIdentifier = null)
        {
            ConnectionId = connectionId;
            UserIdentifier = userIdentifier ?? userId.ToString();
            ThrowOnAbort = throwOnAbort;
            _user = accessToken is null
                ? new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) }, "test"))
                : PrincipalOf(accessToken);
        }

        public bool ThrowOnAbort { get; }
        public int AbortCount { get; private set; }

        public override string ConnectionId { get; }
        public override string? UserIdentifier { get; }
        public override ClaimsPrincipal? User => _user;
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;

        public override void Abort()
        {
            AbortCount++;
            if (ThrowOnAbort)
            {
                throw new InvalidOperationException("simulated abort failure");
            }
        }
    }
}
