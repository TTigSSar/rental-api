using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using RentalPlatform.Api.Controllers.Requests;
using RentalPlatform.Api.Extensions;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.Common;
using RentalPlatform.Application.DTOs;

namespace RentalPlatform.Api.Controllers;

[ApiController]
[Route("api/chat")]
[Authorize]
public sealed class ChatController : ControllerBase
{
    // 7 MB cap on the multipart upload body: the service-layer per-file limit is 5 MB
    // (ChatService.MaxAttachmentBytes, reused from ListingImagesOwnerService), plus headroom
    // for multipart boundary/header overhead and the optional caption field.
    private const long MaxChatImageUploadBytes = 7L * 1024 * 1024;

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

    [HttpPost("conversations/{id:guid}/messages/image")]
    [EnableRateLimiting(RateLimiterExtensions.ImageUploadPolicy)]
    [RequestSizeLimit(MaxChatImageUploadBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxChatImageUploadBytes)]
    [ProducesResponseType(typeof(ChatMessageResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> SendImageMessage(
        Guid id,
        [FromForm] UploadChatImageRequest request,
        CancellationToken cancellationToken)
    {
        Stream? stream = null;

        try
        {
            stream = request.Image.OpenReadStream();

            var serviceRequest = new SendChatImageMessageRequest
            {
                ConversationId = id,
                FileName = request.Image.FileName,
                ContentType = request.Image.ContentType,
                Length = request.Image.Length,
                Content = stream,
                Caption = request.Caption
            };

            var result = await _chatService.SendImageMessageAsync(serviceRequest, cancellationToken);
            if (result.IsSuccess && result.Value is not null)
            {
                return Ok(result.Value);
            }

            return FromError(result.Error);
        }
        finally
        {
            if (stream is not null)
            {
                await stream.DisposeAsync();
            }
        }
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
            "chat.unauthenticated" => Unauthorized(error.ToProblemDetails(StatusCodes.Status401Unauthorized)),
            "chat.user_blocked" => StatusCode(StatusCodes.Status403Forbidden, error.ToProblemDetails(StatusCodes.Status403Forbidden)),
            "chat.not_participant" => StatusCode(StatusCodes.Status403Forbidden, error.ToProblemDetails(StatusCodes.Status403Forbidden)),
            "chat.conversation_not_found" => NotFound(error.ToProblemDetails(StatusCodes.Status404NotFound)),
            "chat.booking_not_found" => NotFound(error.ToProblemDetails(StatusCodes.Status404NotFound)),
            "chat.conversation_closed" => Conflict(error.ToProblemDetails(StatusCodes.Status409Conflict)),
            "chat.message_too_long" => BadRequest(error.ToProblemDetails(StatusCodes.Status400BadRequest)),
            "chat.attachment_too_large" => BadRequest(error.ToProblemDetails(StatusCodes.Status400BadRequest)),
            "chat.attachment_invalid_type" => BadRequest(error.ToProblemDetails(StatusCodes.Status400BadRequest)),
            _ => BadRequest(error.ToProblemDetails(StatusCodes.Status400BadRequest))
        };
    }
}
