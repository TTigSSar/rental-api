using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

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

    public ChatHub(ILogger<ChatHub> logger)
    {
        _logger = logger;
    }

    public override Task OnConnectedAsync()
    {
        _logger.LogDebug(
            "Chat hub connection {ConnectionId} established for user {UserId}",
            Context.ConnectionId,
            Context.UserIdentifier);

        return base.OnConnectedAsync();
    }
}
