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
}
