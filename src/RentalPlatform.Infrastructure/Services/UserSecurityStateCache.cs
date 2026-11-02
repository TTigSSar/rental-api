using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using RentalPlatform.Infrastructure.Persistence;

namespace RentalPlatform.Infrastructure.Services;

/// <summary>What the JWT check needs from a user row (ADR-055): the session version only; a block does not revoke.</summary>
public readonly record struct UserSecurityState(int TokenVersion);

/// <summary>The lean database read behind <see cref="UserSecurityStateCache"/>; a seam so a test can pause it.</summary>
public interface IUserSecurityStateReader
{
    /// <summary>The user's current state, or null when the user does not exist. Throws on a database failure.</summary>
    Task<UserSecurityState?> ReadAsync(Guid userId, CancellationToken cancellationToken);
}

public sealed class UserSecurityStateReader : IUserSecurityStateReader
{
    private readonly IServiceScopeFactory _scopeFactory;

    public UserSecurityStateReader(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task<UserSecurityState?> ReadAsync(Guid userId, CancellationToken cancellationToken)
    {
        // Own scope: the request's DbContext must not be touched from the authentication handler.
        await using var scope = _scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var row = await dbContext.Users
            .AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => new { user.TokenVersion })
            .FirstOrDefaultAsync(cancellationToken);

        return row is null ? null : new UserSecurityState(row.TokenVersion);
    }
}

/// <summary>
/// In-process 60 s cache of the <c>TokenVersion</c> per user, the hot path of the JWT revocation
/// check (ADR-055). Correct for ONE API instance only: another instance would not see this instance's
/// invalidations. Scaling out reopens ADR-055.
///
/// A plain "evict on change" is not enough: a populate that read the database before a rotation committed
/// could store its stale value after the eviction. So every invalidation bumps a global epoch under the same
/// lock a populate takes to store; a populate stores only if the epoch is the one it saw before its read.
/// A missing user is never cached.
/// </summary>
public sealed class UserSecurityStateCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    private readonly IMemoryCache _cache;
    private readonly IUserSecurityStateReader _reader;
    private readonly object _gate = new();
    private long _epoch;

    public UserSecurityStateCache(IMemoryCache cache, IUserSecurityStateReader reader)
    {
        _cache = cache;
        _reader = reader;
    }

    /// <summary>The user's current state (cached or freshly read), or null when the user does not exist.</summary>
    public async Task<UserSecurityState?> GetAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var key = Key(userId);
        if (_cache.TryGetValue(key, out UserSecurityState cached))
        {
            return cached;
        }

        long epochBeforeRead;
        lock (_gate)
        {
            epochBeforeRead = _epoch;
        }

        var state = await _reader.ReadAsync(userId, cancellationToken);
        if (state is null)
        {
            return null;
        }

        lock (_gate)
        {
            if (_epoch == epochBeforeRead)
            {
                _cache.Set(key, state.Value, Ttl);
            }
        }

        // The value just read is returned either way: it is at least as new as the caller's token check needs.
        return state;
    }

    /// <summary>Call synchronously AFTER the database commit that changed the user's state.</summary>
    public void Invalidate(Guid userId)
    {
        lock (_gate)
        {
            _epoch++;
            _cache.Remove(Key(userId));
        }
    }

    private static string Key(Guid userId) => $"user-security:{userId:N}";
}
