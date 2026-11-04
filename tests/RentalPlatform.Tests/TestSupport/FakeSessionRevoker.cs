using RentalPlatform.Application.Abstractions;

namespace RentalPlatform.Tests.TestSupport;

// Records revocations; for the unit tests that do not run the real cache/registry.
public sealed class FakeSessionRevoker : ISessionRevoker
{
    public List<Guid> Revoked { get; } = new();

    public Task RevokeAsync(Guid userId)
    {
        Revoked.Add(userId);
        return Task.CompletedTask;
    }
}
