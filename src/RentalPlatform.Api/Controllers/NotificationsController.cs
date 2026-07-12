using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RentalPlatform.Api.Extensions;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.Common;
using RentalPlatform.Application.DTOs;

namespace RentalPlatform.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public sealed class NotificationsController : ControllerBase
{
    private readonly INotificationsService _notificationsService;

    public NotificationsController(INotificationsService notificationsService)
    {
        _notificationsService = notificationsService;
    }

    [HttpGet]
    [ProducesResponseType(typeof(NotificationFeedResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetFeed(
        [FromQuery] string? filter,
        [FromQuery] string? cursor,
        CancellationToken cancellationToken)
    {
        var result = await _notificationsService.GetFeedAsync(filter, cursor, cancellationToken);
        if (result.IsSuccess && result.Value is not null)
        {
            return Ok(result.Value);
        }

        return FromError(result.Error);
    }

    [HttpGet("unread-count")]
    [ProducesResponseType(typeof(NotificationUnreadCountResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetUnreadCount(CancellationToken cancellationToken)
    {
        var result = await _notificationsService.GetUnreadCountAsync(cancellationToken);
        if (result.IsSuccess && result.Value is not null)
        {
            return Ok(result.Value);
        }

        return FromError(result.Error);
    }

    [HttpPost("{id:guid}/read")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> MarkRead(Guid id, CancellationToken cancellationToken)
    {
        var result = await _notificationsService.MarkReadAsync(id, cancellationToken);
        if (result.IsSuccess)
        {
            return NoContent();
        }

        return FromError(result.Error);
    }

    [HttpPost("read-all")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> MarkAllRead(CancellationToken cancellationToken)
    {
        var result = await _notificationsService.MarkAllReadAsync(cancellationToken);
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
            "notification.unauthenticated" => Unauthorized(error.ToProblemDetails(StatusCodes.Status401Unauthorized)),
            "notification.not_found" => NotFound(error.ToProblemDetails(StatusCodes.Status404NotFound)),
            _ => BadRequest(error.ToProblemDetails(StatusCodes.Status400BadRequest))
        };
    }
}
