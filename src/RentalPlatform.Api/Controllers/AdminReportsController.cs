using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RentalPlatform.Api.Extensions;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.Common;
using RentalPlatform.Application.DTOs;
using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Api.Controllers;

// Admin console Phase 4: the Reports & flags screen.
[ApiController]
[Route("api/admin/reports")]
[Authorize(Roles = nameof(UserRole.Admin))]
public sealed class AdminReportsController : ControllerBase
{
    private readonly IAdminReportsService _adminReportsService;

    public AdminReportsController(IAdminReportsService adminReportsService)
    {
        _adminReportsService = adminReportsService;
    }

    [HttpGet]
    [ProducesResponseType(typeof(AdminReportQueueResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<AdminReportQueueResponse>> GetQueue(
        [FromQuery] AdminReportQueueFilter filter,
        CancellationToken cancellationToken)
    {
        var result = await _adminReportsService.GetQueueAsync(filter, cancellationToken);
        if (result.IsSuccess && result.Value is not null)
        {
            return Ok(result.Value);
        }

        return FromError(result.Error);
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(AdminReportRowResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AdminReportRowResponse>> GetById(
        Guid id,
        CancellationToken cancellationToken)
    {
        var result = await _adminReportsService.GetByIdAsync(id, cancellationToken);
        if (result.IsSuccess && result.Value is not null)
        {
            return Ok(result.Value);
        }

        return FromError(result.Error);
    }

    [HttpPost("{id:guid}/resolve")]
    [ProducesResponseType(typeof(AdminReportRowResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AdminReportRowResponse>> Resolve(
        Guid id,
        [FromBody] ReportActionRequest? request,
        CancellationToken cancellationToken)
    {
        var result = await _adminReportsService.ResolveAsync(id, request?.Note, cancellationToken);
        if (result.IsSuccess && result.Value is not null)
        {
            return Ok(result.Value);
        }

        return FromError(result.Error);
    }

    [HttpPost("{id:guid}/dismiss")]
    [ProducesResponseType(typeof(AdminReportRowResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AdminReportRowResponse>> Dismiss(
        Guid id,
        [FromBody] ReportActionRequest? request,
        CancellationToken cancellationToken)
    {
        var result = await _adminReportsService.DismissAsync(id, request?.Note, cancellationToken);
        if (result.IsSuccess && result.Value is not null)
        {
            return Ok(result.Value);
        }

        return FromError(result.Error);
    }

    [HttpPost("{id:guid}/reopen")]
    [ProducesResponseType(typeof(AdminReportRowResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AdminReportRowResponse>> Reopen(
        Guid id,
        CancellationToken cancellationToken)
    {
        var result = await _adminReportsService.ReopenAsync(id, cancellationToken);
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
            "admin.report_not_found" => NotFound(error.ToProblemDetails(StatusCodes.Status404NotFound)),
            "admin.report_invalid_target_filter" => BadRequest(error.ToProblemDetails(StatusCodes.Status400BadRequest)),
            "admin.report_target_filter_incomplete" => BadRequest(error.ToProblemDetails(StatusCodes.Status400BadRequest)),
            _ => BadRequest(error.ToProblemDetails(StatusCodes.Status400BadRequest))
        };
    }
}
