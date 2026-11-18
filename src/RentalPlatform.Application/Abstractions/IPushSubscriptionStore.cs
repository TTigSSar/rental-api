using RentalPlatform.Domain.Entities;

namespace RentalPlatform.Application.Abstractions;

public interface IPushSubscriptionStore
{
    Task<User?> FindUserByIdAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Insert-or-update by endpoint hash: a row that belongs to another user moves to <paramref name="userId"/>.
    /// Afterwards the user keeps at most <paramref name="maxPerUser"/> rows, the oldest by CreatedAt evicted.
    /// </summary>
    Task UpsertAsync(
        Guid userId,
        string endpoint,
        byte[] endpointHash,
        string p256dh,
        string auth,
        DateTime now,
        int maxPerUser,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes only the caller's own row; true when something was removed.</summary>
    Task<bool> DeleteAsync(Guid userId, byte[] endpointHash, CancellationToken cancellationToken = default);
}
