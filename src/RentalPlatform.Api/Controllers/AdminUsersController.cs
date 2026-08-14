using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RentalPlatform.Api.Extensions;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.Common;
using RentalPlatform.Application.DTOs;
using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Api.Controllers;

// Admin console Phase 3: the Users screen.
[ApiController]
[Route("api/admin/users")]
[Authorize(Roles = nameof(UserRole.Admin))]
public sealed class AdminUsersController : ControllerBase
{
    private readonly IAdminUsersService _adminUsersService;

    public AdminUsersController(IAdminUsersService adminUsersService)
    {
        _adminUsersService = adminUsersService;
    }

    [HttpGet]
    [ProducesResponseType(typeof(AdminUserQueueResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<AdminUserQueueResponse>> GetQueue(
        [FromQuery] AdminUserQueueFilter filter,
        CancellationToken cancellationToken)
    {
        var result = await _adminUsersService.GetQueueAsync(filter, cancellationToken);
        if (result.IsSuccess && result.Value is not null)
        {
            return Ok(result.Value);
        }

        return FromError(result.Error);
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(AdminUserSummaryResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AdminUserSummaryResponse>> GetById(
        Guid id,
        CancellationToken cancellationToken)
    {
        var result = await _adminUsersService.GetByIdAsync(id, cancellationToken);
        if (result.IsSuccess && result.Value is not null)
        {
            return Ok(result.Value);
        }

        return FromError(result.Error);
    }

    [HttpPost("{id:guid}/verify")]
    [ProducesResponseType(typeof(AdminUserSummaryResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AdminUserSummaryResponse>> Verify(
        Guid id,
        CancellationToken cancellationToken)
    {
        var result = await _adminUsersService.VerifyAsync(id, cancellationToken);
        if (result.IsSuccess && result.Value is not null)
        {
            return Ok(result.Value);
        }

        return FromError(result.Error);
    }

    [HttpPost("{id:guid}/suspend")]
    [ProducesResponseType(typeof(AdminUserSummaryResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AdminUserSummaryResponse>> Suspend(
        Guid id,
        CancellationToken cancellationToken)
    {
        var result = await _adminUsersService.SuspendAsync(id, cancellationToken);
        if (result.IsSuccess && result.Value is not null)
        {
            return Ok(result.Value);
        }

        return FromError(result.Error);
    }

    [HttpPost("{id:guid}/reactivate")]
    [ProducesResponseType(typeof(AdminUserSummaryResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AdminUserSummaryResponse>> Reactivate(
        Guid id,
        CancellationToken cancellationToken)
    {
        var result = await _adminUsersService.ReactivateAsync(id, cancellationToken);
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
            "admin.cannot_suspend_self" => BadRequest(error.ToProblemDetails(StatusCodes.Status400BadRequest)),
            "admin.cannot_suspend_admin" => BadRequest(error.ToProblemDetails(StatusCodes.Status400BadRequest)),
            _ => BadRequest(error.ToProblemDetails(StatusCodes.Status400BadRequest))
        };
    }
}
