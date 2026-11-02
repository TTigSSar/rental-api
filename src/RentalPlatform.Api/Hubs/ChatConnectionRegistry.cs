using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;

namespace RentalPlatform.Api.Hubs;

/// <summary>
/// Open chat hub connections by user id, then connection id (a user may have several tabs or browsers),
/// so a session revocation can end them (ADR-055). Singleton.
/// </summary>
public sealed class ChatConnectionRegistry
{
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, HubCallerContext>> _connections = new();

    // Register/Unregister take it so that dropping an empty bucket cannot race a registration into
    // a bucket that was just removed. Reads (Snapshot) stay lock-free.
    private readonly object _gate = new();
    private readonly ILogger<ChatConnectionRegistry> _logger;

    public ChatConnectionRegistry(ILogger<ChatConnectionRegistry> logger)
    {
        _logger = logger;
    }

    public void Register(Guid userId, HubCallerContext context)
    {
        lock (_gate)
        {
            var bucket = _connections.GetOrAdd(userId, _ => new ConcurrentDictionary<string, HubCallerContext>());
            bucket[context.ConnectionId] = context;
        }
    }

    public void Unregister(Guid userId, string connectionId)
    {
        lock (_gate)
        {
            if (_connections.TryGetValue(userId, out var bucket)
                && bucket.TryRemove(connectionId, out _)
                && bucket.IsEmpty)
            {
                _connections.TryRemove(userId, out _);
            }
        }
    }

    public IReadOnlyList<HubCallerContext> Snapshot(Guid userId) =>
        _connections.TryGetValue(userId, out var bucket) ? bucket.Values.ToArray() : Array.Empty<HubCallerContext>();

    /// <summary>
    /// Best-effort <see cref="HubCallerContext.Abort"/> of every connection the user has right now. Each abort
    /// is isolated: one failing never stops the others, and nothing is thrown to the caller.
    /// </summary>
    public void AbortUser(Guid userId)
    {
        foreach (var context in Snapshot(userId))
        {
            try
            {
                context.Abort();
            }
            catch (Exception exception)
            {
                // The token-lifetime bound (CloseOnAuthenticationExpiration) is the backstop (ADR-055).
                _logger.LogWarning(
                    exception,
                    "Could not abort chat connection {ConnectionId} of user {UserId} after a session revocation",
                    context.ConnectionId,
                    userId);
            }
        }
    }
}
