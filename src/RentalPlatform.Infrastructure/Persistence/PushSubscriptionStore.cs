using Microsoft.EntityFrameworkCore;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Domain.Entities;

namespace RentalPlatform.Infrastructure.Persistence;

public sealed class PushSubscriptionStore : IPushSubscriptionStore
{
    private readonly AppDbContext _dbContext;

    public PushSubscriptionStore(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<User?> FindUserByIdAsync(Guid userId, CancellationToken cancellationToken = default) =>
        _dbContext.Users.AsNoTracking().FirstOrDefaultAsync(user => user.Id == userId, cancellationToken);

    public async Task UpsertAsync(
        Guid userId,
        string endpoint,
        byte[] endpointHash,
        string p256dh,
        string auth,
        DateTime now,
        int maxPerUser,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await UpsertOnceAsync(userId, endpoint, endpointHash, p256dh, auth, now, cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Two concurrent registrations of the same endpoint: the unique index let one win. The loser
            // re-reads and updates the winner's row.
            _dbContext.ChangeTracker.Clear();
            await UpsertOnceAsync(userId, endpoint, endpointHash, p256dh, auth, now, cancellationToken);
        }

        await EvictOverCapAsync(userId, maxPerUser, cancellationToken);
    }

    private async Task UpsertOnceAsync(
        Guid userId,
        string endpoint,
        byte[] endpointHash,
        string p256dh,
        string auth,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var existing = await _dbContext.PushSubscriptions
            .FirstOrDefaultAsync(subscription => subscription.EndpointHash == endpointHash, cancellationToken);

        if (existing is null)
        {
            _dbContext.PushSubscriptions.Add(new PushSubscription
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Endpoint = endpoint,
                EndpointHash = endpointHash,
                P256dh = p256dh,
                Auth = auth,
                CreatedAt = now
            });
        }
        else
        {
            // Same device, possibly a different account now (shared browser): the row moves to the caller.
            existing.UserId = userId;
            existing.Endpoint = endpoint;
            existing.P256dh = p256dh;
            existing.Auth = auth;
            existing.CreatedAt = now;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task EvictOverCapAsync(Guid userId, int maxPerUser, CancellationToken cancellationToken)
    {
        var rows = await _dbContext.PushSubscriptions
            .AsNoTracking()
            .Where(subscription => subscription.UserId == userId)
            .Select(subscription => new { subscription.Id, subscription.CreatedAt })
            .ToListAsync(cancellationToken);

        if (rows.Count <= maxPerUser)
        {
            return;
        }

        var evict = rows
            .OrderBy(row => row.CreatedAt)
            .Take(rows.Count - maxPerUser)
            .Select(row => row.Id)
            .ToList();

        await _dbContext.PushSubscriptions
            .Where(subscription => evict.Contains(subscription.Id))
            .ExecuteDeleteAsync(cancellationToken);
    }

    public async Task<bool> DeleteAsync(Guid userId, byte[] endpointHash, CancellationToken cancellationToken = default)
    {
        var removed = await _dbContext.PushSubscriptions
            .Where(subscription => subscription.UserId == userId && subscription.EndpointHash == endpointHash)
            .ExecuteDeleteAsync(cancellationToken);
        return removed > 0;
    }
}
