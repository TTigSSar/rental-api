using Microsoft.EntityFrameworkCore;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Domain.Entities;

namespace RentalPlatform.Infrastructure.Persistence;

public sealed class UserAuthStore : IUserAuthStore
{
    private readonly AppDbContext _dbContext;

    public UserAuthStore(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<bool> EmailExistsAsync(string email, CancellationToken cancellationToken = default) =>
        _dbContext.Users.AnyAsync(user => user.Email == email, cancellationToken);

    // Include(HomeDistrict) on every find here — CurrentUserResponse.HomePoint.District needs it
    // and every one of these methods eventually feeds a MapUser call (register/login/external/me/
    // preferred-language/home-point updates all return a CurrentUserResponse).
    public Task<User?> FindByEmailAsync(string email, CancellationToken cancellationToken = default) =>
        _dbContext.Users.Include(user => user.HomeDistrict).FirstOrDefaultAsync(user => user.Email == email, cancellationToken);

    public Task<User?> FindByExternalProviderAsync(string provider, string externalProviderId, CancellationToken cancellationToken = default) =>
        _dbContext.Users.Include(user => user.HomeDistrict).FirstOrDefaultAsync(
            user => user.ExternalAuthProvider == provider && user.ExternalProviderId == externalProviderId,
            cancellationToken);

    public Task<User?> FindByIdAsync(Guid userId, CancellationToken cancellationToken = default) =>
        _dbContext.Users.Include(user => user.HomeDistrict).FirstOrDefaultAsync(user => user.Id == userId, cancellationToken);

    public async Task AddAsync(User user, CancellationToken cancellationToken = default) =>
        await _dbContext.Users.AddAsync(user, cancellationToken);

    public async Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
        await _dbContext.SaveChangesAsync(cancellationToken);

    public async Task<bool> TryChangePasswordAsync(User user, string newPasswordHash, CancellationToken cancellationToken = default)
    {
        var seenHash = user.PasswordHash;
        var seenVersion = user.TokenVersion;

        var updated = await _dbContext.Users
            .Where(candidate => candidate.Id == user.Id
                                && candidate.PasswordHash == seenHash
                                && candidate.TokenVersion == seenVersion
                                && !candidate.IsBlocked)
            .ExecuteUpdateAsync(set => set
                .SetProperty(candidate => candidate.PasswordHash, newPasswordHash)
                .SetProperty(candidate => candidate.TokenVersion, candidate => candidate.TokenVersion + 1), cancellationToken);

        var entry = _dbContext.Entry(user);
        if (updated != 1)
        {
            // The tracked copy is stale; the caller's re-read must hit the database, not the tracker.
            entry.State = EntityState.Detached;
            return false;
        }

        // Mirror the saved values without leaving the entity Modified (a later SaveChanges must not re-send them).
        entry.Property(candidate => candidate.PasswordHash).CurrentValue = newPasswordHash;
        entry.Property(candidate => candidate.PasswordHash).OriginalValue = newPasswordHash;
        entry.Property(candidate => candidate.TokenVersion).CurrentValue = seenVersion + 1;
        entry.Property(candidate => candidate.TokenVersion).OriginalValue = seenVersion + 1;
        entry.Property(candidate => candidate.PasswordHash).IsModified = false;
        entry.Property(candidate => candidate.TokenVersion).IsModified = false;
        return true;
    }

    public async Task<bool> TrySaveChangesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException exception) when (EmailVerificationStore.IsUniqueViolation(exception))
        {
            // The rejected values must not linger in the tracker and be re-sent by a later save.
            _dbContext.ChangeTracker.Clear();
            return false;
        }
    }
}
