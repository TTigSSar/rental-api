using RentalPlatform.Application.Abstractions;
using RentalPlatform.Infrastructure.Services;

namespace RentalPlatform.Api.Hubs;

/// <summary>
/// <see cref="ISessionRevoker"/> over the in-process security-state cache and the chat connection registry
/// (ADR-055). The cache is invalidated first, so a connection that registers after the abort snapshot is
/// guaranteed to see the new state in its connect-time re-check.
/// </summary>
public sealed class SessionRevoker : ISessionRevoker
{
    private readonly UserSecurityStateCache _cache;
    private readonly ChatConnectionRegistry _registry;

    public SessionRevoker(UserSecurityStateCache cache, ChatConnectionRegistry registry)
    {
        _cache = cache;
        _registry = registry;
    }

    public Task RevokeAsync(Guid userId)
    {
        _cache.Invalidate(userId);
        _registry.AbortUser(userId);
        return Task.CompletedTask;
    }
}
