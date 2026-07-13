using RentalPlatform.Application.Abstractions;
using RentalPlatform.Domain.Entities;

namespace RentalPlatform.Tests.TestSupport;

// In-memory double for IUserAuthStore. Avoids spinning up a DbContext for tests that only
// need to observe "was a user added / does one already exist" without exercising EF Core.
public sealed class FakeUserAuthStore : IUserAuthStore
{
    private readonly Dictionary<string, User> _usersByEmail = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<User> _pendingAdds = new();

    public IReadOnlyCollection<User> Users => _usersByEmail.Values;

    public FakeUserAuthStore Seed(params User[] users)
    {
        foreach (var user in users)
        {
            _usersByEmail[user.Email] = user;
        }

        return this;
    }

    public Task<bool> EmailExistsAsync(string email, CancellationToken cancellationToken = default) =>
        Task.FromResult(_usersByEmail.ContainsKey(email));

    public Task<User?> FindByEmailAsync(string email, CancellationToken cancellationToken = default) =>
        Task.FromResult(_usersByEmail.TryGetValue(email, out var user) ? user : null);

    public Task<User?> FindByExternalProviderAsync(string provider, string externalProviderId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_usersByEmail.Values.FirstOrDefault(
            user => user.ExternalAuthProvider == provider && user.ExternalProviderId == externalProviderId));

    public Task<User?> FindByIdAsync(Guid userId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_usersByEmail.Values.FirstOrDefault(user => user.Id == userId));

    public Task AddAsync(User user, CancellationToken cancellationToken = default)
    {
        _pendingAdds.Add(user);
        return Task.CompletedTask;
    }

    // Mirrors AppDbContext.SaveChangesAsync: pending adds only become visible (e.g. to
    // EmailExistsAsync) once "saved", so tests can assert AddAsync alone didn't commit anything.
    public Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        foreach (var user in _pendingAdds)
        {
            _usersByEmail[user.Email] = user;
        }

        _pendingAdds.Clear();
        return Task.CompletedTask;
    }
}
