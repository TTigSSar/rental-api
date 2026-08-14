using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RentalPlatform.Api.Extensions;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.Common;
using RentalPlatform.Application.DTOs;
using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Api.Controllers;

// Admin console Phase 5: the Overview screen (stat tiles, Queue health panel, recent-activity feed).
[ApiController]
[Route("api/admin/overview")]
[Authorize(Roles = nameof(UserRole.Admin))]
public sealed class AdminOverviewController : ControllerBase
{
    private readonly IAdminOverviewService _adminOverviewService;

    public AdminOverviewController(IAdminOverviewService adminOverviewService)
    {
        _adminOverviewService = adminOverviewService;
    }

    [HttpGet]
    [ProducesResponseType(typeof(AdminOverviewResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<AdminOverviewResponse>> GetOverview(CancellationToken cancellationToken)
    {
        var result = await _adminOverviewService.GetOverviewAsync(cancellationToken);
        if (result.IsSuccess && result.Value is not null)
        {
            return Ok(result.Value);
        }

        return FromError(result.Error);
    }

    [HttpGet("activity")]
    [ProducesResponseType(typeof(AdminActivityFeedResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<AdminActivityFeedResponse>> GetActivity(
        [FromQuery] int take,
        CancellationToken cancellationToken)
    {
        var result = await _adminOverviewService.GetActivityFeedAsync(take, cancellationToken);
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
            _ => BadRequest(error.ToProblemDetails(StatusCodes.Status400BadRequest))
        };
    }
}
