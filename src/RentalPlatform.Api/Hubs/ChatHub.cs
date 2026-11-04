using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using RentalPlatform.Infrastructure.Services;

namespace RentalPlatform.Api.Hubs;

/// <summary>
/// Realtime transport for chat. No client-invokable methods: targeting is by user id
/// (the default <see cref="ClaimTypes.NameIdentifier"/>-based user identifier, which the JWT
/// sets to the user's Guid — see JwtTokenService), so no group-join dance is needed. Server
/// pushes only, via <see cref="RentalPlatform.Application.Abstractions.IChatRealtimeNotifier"/>.
/// </summary>
[Authorize]
public sealed class ChatHub : Hub
{
    private readonly ILogger<ChatHub> _logger;
    private readonly ChatConnectionRegistry _registry;
    private readonly UserSecurityStateCache _securityState;

    public ChatHub(ILogger<ChatHub> logger, ChatConnectionRegistry registry, UserSecurityStateCache securityState)
    {
        _logger = logger;
        _registry = registry;
        _securityState = securityState;
    }

    public override async Task OnConnectedAsync()
    {
        if (!Guid.TryParse(Context.UserIdentifier, out var userId))
        {
            Context.Abort();
            return;
        }

        // Register FIRST, then re-check the token's version (ADR-055): a rotation that committed between
        // the handshake's authentication and now either snapshots this connection (and aborts it) or has
        // already invalidated the cache, so this check sees the new version. Either way it cannot slip through.
        _registry.Register(userId, Context);

        bool accepted;
        try
        {
            accepted = Context.User is { } principal
                       && await SessionTokenValidator.IsAcceptedAsync(principal, _securityState, Context.ConnectionAborted);
        }
        catch (Exception exception)
        {
            // Fail closed, like the HTTP pipeline: no connection without a verified session.
            _logger.LogError(exception, "Session re-check failed for chat connection {ConnectionId}", Context.ConnectionId);
            accepted = false;
        }

        if (!accepted)
        {
            Context.Abort();
            return;
        }

        _logger.LogDebug(
            "Chat hub connection {ConnectionId} established for user {UserId}",
            Context.ConnectionId,
            Context.UserIdentifier);

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (Guid.TryParse(Context.UserIdentifier, out var userId))
        {
            _registry.Unregister(userId, Context.ConnectionId);
        }

        await base.OnDisconnectedAsync(exception);
    }
}
