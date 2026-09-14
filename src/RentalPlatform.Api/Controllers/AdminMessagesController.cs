using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RentalPlatform.Api.Extensions;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.Common;
using RentalPlatform.Application.DTOs;
using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Api.Controllers;

// Admin console: the Messages screen (moderation-only conversations — see ADR-016 §4 /
// Conversation.Kind == Moderation). Everything else (send/read/detail) is the existing
// /api/chat surface, reused unchanged by the admin UI — this controller only covers the thread
// queue and get-or-create.
[ApiController]
[Route("api/admin/messages")]
[Authorize(Roles = nameof(UserRole.Admin))]
public sealed class AdminMessagesController : ControllerBase
{
    private readonly IAdminMessagesService _adminMessagesService;

    public AdminMessagesController(IAdminMessagesService adminMessagesService)
    {
        _adminMessagesService = adminMessagesService;
    }

    [HttpGet("threads")]
    [ProducesResponseType(typeof(AdminMessageThreadQueueResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    // Bound as "query", not "filter": ASP.NET Core's complex-type query-string binder falls back to
    // binding each property by its bare name (Search, Page, PageSize, Filter) only when the value
    // provider has no key that exactly matches the parameter's own name. AdminMessageThreadFilter's
    // wire shape includes a "?filter=" key (see the DTO's Filter property) — when the action
    // parameter was itself named "filter", that key's mere presence made the binder skip the
    // fallback entirely, so NONE of the properties bound (not just Filter) whenever the request
    // included "?filter=...". Renaming the parameter removes the name collision without touching
    // the "?filter=" wire value itself.
    public async Task<ActionResult<AdminMessageThreadQueueResponse>> GetThreads(
        [FromQuery] AdminMessageThreadFilter query,
        CancellationToken cancellationToken)
    {
        var result = await _adminMessagesService.GetThreadsAsync(query, cancellationToken);
        if (result.IsSuccess && result.Value is not null)
        {
            return Ok(result.Value);
        }

        return FromError(result.Error);
    }

    [HttpPost("threads")]
    [ProducesResponseType(typeof(AdminMessageThreadResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AdminMessageThreadResponse>> OpenThread(
        [FromBody] OpenAdminMessageThreadRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _adminMessagesService.OpenThreadAsync(request.UserId, cancellationToken);
        if (result.IsSuccess && result.Value is not null)
        {
            return Ok(result.Value);
        }

        return FromError(result.Error);
    }

    private ActionResult FromError(ServiceError? error)
    {
        if (error is null)
        {
            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "Unexpected error.");
        }

        return error.Code switch
        {
            "admin.unauthenticated" => Unauthorized(error.ToProblemDetails(StatusCodes.Status401Unauthorized)),
            "admin.forbidden" => StatusCode(StatusCodes.Status403Forbidden, error.ToProblemDetails(StatusCodes.Status403Forbidden)),
            "admin.user_not_found" => NotFound(error.ToProblemDetails(StatusCodes.Status404NotFound)),
            "admin.cannot_message_self" => BadRequest(error.ToProblemDetails(StatusCodes.Status400BadRequest)),
            "admin.cannot_message_admin" => BadRequest(error.ToProblemDetails(StatusCodes.Status400BadRequest)),
            _ => BadRequest(error.ToProblemDetails(StatusCodes.Status400BadRequest))
        };
    }
}
