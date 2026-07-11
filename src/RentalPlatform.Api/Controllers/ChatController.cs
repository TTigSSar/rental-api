using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.Common;
using RentalPlatform.Application.DTOs;

namespace RentalPlatform.Api.Controllers;

[ApiController]
[Route("api/chat")]
[Authorize]
public sealed class ChatController : ControllerBase
{
    private readonly IChatService _chatService;

    public ChatController(IChatService chatService)
    {
        _chatService = chatService;
    }

    [HttpGet("conversations")]
    [ProducesResponseType(typeof(IReadOnlyCollection<ChatConversationResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetConversations(CancellationToken cancellationToken)
    {
        var result = await _chatService.GetConversationsAsync(cancellationToken);
        if (result.IsSuccess && result.Value is not null)
        {
            return Ok(result.Value);
        }

        return FromError(result.Error);
    }

    [HttpGet("conversations/{id:guid}")]
    [ProducesResponseType(typeof(ChatConversationDetailsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetConversation(
        Guid id,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        CancellationToken cancellationToken)
    {
        var result = await _chatService.GetConversationAsync(id, page, pageSize, cancellationToken);
        if (result.IsSuccess && result.Value is not null)
        {
            return Ok(result.Value);
        }

        return FromError(result.Error);
    }

    [HttpPost("conversations/from-booking/{bookingId:guid}")]
    [ProducesResponseType(typeof(ChatConversationDetailsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetOrCreateFromBooking(Guid bookingId, CancellationToken cancellationToken)
    {
        var result = await _chatService.GetOrCreateForBookingAsync(bookingId, cancellationToken);
        if (result.IsSuccess && result.Value is not null)
        {
            return Ok(result.Value);
        }

        return FromError(result.Error);
    }

    [HttpPost("messages")]
    [ProducesResponseType(typeof(ChatMessageResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> SendMessage(
        [FromBody] SendChatMessageRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _chatService.SendMessageAsync(request, cancellationToken);
        if (result.IsSuccess && result.Value is not null)
        {
            return Ok(result.Value);
        }

        return FromError(result.Error);
    }

    [HttpPost("conversations/{id:guid}/read")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> MarkRead(Guid id, CancellationToken cancellationToken)
    {
        var result = await _chatService.MarkReadAsync(id, cancellationToken);
        if (result.IsSuccess)
        {
            return NoContent();
        }

        return FromError(result.Error);
    }

    private IActionResult FromError(ServiceError? error)
    {
        if (error is null)
        {
            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "Unexpected error.");
        }

        return error.Code switch
        {
            "chat.unauthenticated" => Unauthorized(ToProblemDetails(StatusCodes.Status401Unauthorized, error)),
            "chat.user_blocked" => StatusCode(StatusCodes.Status403Forbidden, ToProblemDetails(StatusCodes.Status403Forbidden, error)),
            "chat.not_participant" => StatusCode(StatusCodes.Status403Forbidden, ToProblemDetails(StatusCodes.Status403Forbidden, error)),
            "chat.conversation_not_found" => NotFound(ToProblemDetails(StatusCodes.Status404NotFound, error)),
            "chat.booking_not_found" => NotFound(ToProblemDetails(StatusCodes.Status404NotFound, error)),
            "chat.conversation_closed" => Conflict(ToProblemDetails(StatusCodes.Status409Conflict, error)),
            "chat.message_too_long" => BadRequest(ToProblemDetails(StatusCodes.Status400BadRequest, error)),
            _ => BadRequest(ToProblemDetails(StatusCodes.Status400BadRequest, error))
        };
    }

    private static ProblemDetails ToProblemDetails(int statusCode, ServiceError error) => new()
    {
        Status = statusCode,
        Title = error.Message,
        Type = error.Code
    };
}
