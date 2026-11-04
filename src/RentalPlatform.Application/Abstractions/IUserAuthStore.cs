using RentalPlatform.Domain.Entities;

namespace RentalPlatform.Application.Abstractions;

public interface IUserAuthStore
{
    Task<bool> EmailExistsAsync(string email, CancellationToken cancellationToken = default);
    Task<User?> FindByEmailAsync(string email, CancellationToken cancellationToken = default);
    Task<User?> FindByExternalProviderAsync(string provider, string externalProviderId, CancellationToken cancellationToken = default);
    Task<User?> FindByIdAsync(Guid userId, CancellationToken cancellationToken = default);
    Task AddAsync(User user, CancellationToken cancellationToken = default);
    Task SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// SaveChanges that reports a unique-index violation as <c>false</c> (the tracked changes are
    /// discarded) instead of throwing. Any other database failure still propagates.
    /// </summary>
    Task<bool> TrySaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Password change (ADR-055): one conditional UPDATE, <c>SET PasswordHash=@new, TokenVersion=TokenVersion+1
    /// WHERE Id AND PasswordHash=@seenHash AND TokenVersion=@seenVersion AND IsBlocked=0</c>, where the seen
    /// values are the ones on <paramref name="user"/>. True (exactly one row): <paramref name="user"/> now holds
    /// the new hash and version, as saved. False: the row changed, was blocked or is gone since it was read;
    /// <paramref name="user"/> is detached and the caller re-reads.
    /// </summary>
    Task<bool> TryChangePasswordAsync(User user, string newPasswordHash, CancellationToken cancellationToken = default);
}
