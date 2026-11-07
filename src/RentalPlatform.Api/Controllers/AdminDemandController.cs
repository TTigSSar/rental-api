using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RentalPlatform.Api.Extensions;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.Common;
using RentalPlatform.Application.DTOs;
using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Api.Controllers;

// P3-3: aggregated booking-request demand per Yerevan day x district x category. Admin only, no screen.
[ApiController]
[Route("api/admin/demand")]
[Authorize(Roles = nameof(UserRole.Admin))]
public sealed class AdminDemandController : ControllerBase
{
    private readonly IAdminDemandService _adminDemandService;

    public AdminDemandController(IAdminDemandService adminDemandService)
    {
        _adminDemandService = adminDemandService;
    }

    // from/to are bound as strings (not DateOnly) so a malformed value reaches the service and comes back
    // as 400 admin.demand_range_invalid instead of a model-binding ProblemDetails without an errorCode.
    [HttpGet]
    [ProducesResponseType(typeof(DemandAnalyticsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<DemandAnalyticsResponse>> GetDemand(
        [FromQuery] string? from,
        [FromQuery] string? to,
        CancellationToken cancellationToken)
    {
        var result = await _adminDemandService.GetDemandAsync(from, to, cancellationToken);
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
            "admin.demand_range_invalid" => BadRequest(error.ToProblemDetails(StatusCodes.Status400BadRequest)),
            _ => BadRequest(error.ToProblemDetails(StatusCodes.Status400BadRequest))
        };
    }
}
