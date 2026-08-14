using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RentalPlatform.Api.Extensions;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.Common;
using RentalPlatform.Application.DTOs;

namespace RentalPlatform.Api.Controllers;

// Admin console Phase 4: user-facing report submission (the "Report" action a renter/owner takes
// against a listing, a user, or a message). Any authenticated, non-blocked user.
[ApiController]
[Route("api/reports")]
[Authorize]
public sealed class ReportsController : ControllerBase
{
    private readonly IReportsService _reportsService;

    public ReportsController(IReportsService reportsService)
    {
        _reportsService = reportsService;
    }

    [HttpPost]
    [ProducesResponseType(typeof(ReportResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ReportResponse>> Create(
        [FromBody] CreateReportRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _reportsService.CreateAsync(request, cancellationToken);
        if (result.IsSuccess && result.Value is not null)
        {
            return StatusCode(StatusCodes.Status201Created, result.Value);
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
            "report.unauthenticated" => Unauthorized(error.ToProblemDetails(StatusCodes.Status401Unauthorized)),
            "report.user_blocked" => StatusCode(StatusCodes.Status403Forbidden, error.ToProblemDetails(StatusCodes.Status403Forbidden)),
            "report.target_not_found" => NotFound(error.ToProblemDetails(StatusCodes.Status404NotFound)),
            "report.already_reported" => Conflict(error.ToProblemDetails(StatusCodes.Status409Conflict)),
            _ => BadRequest(error.ToProblemDetails(StatusCodes.Status400BadRequest))
        };
    }
}
