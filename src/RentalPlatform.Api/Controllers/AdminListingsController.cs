using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RentalPlatform.Api.Extensions;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.Common;
using RentalPlatform.Application.DTOs;
using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Api.Controllers;

[ApiController]
[Route("api/admin/listings")]
[Authorize(Roles = nameof(UserRole.Admin))]
public sealed class AdminListingsController : ControllerBase
{
    private readonly IAdminListingsService _adminListingsService;

    public AdminListingsController(IAdminListingsService adminListingsService)
    {
        _adminListingsService = adminListingsService;
    }

    [HttpGet("pending")]
    [ProducesResponseType(typeof(IReadOnlyCollection<PendingListingForReviewResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyCollection<PendingListingForReviewResponse>>> GetPending(
        CancellationToken cancellationToken)
    {
        var result = await _adminListingsService.GetPendingAsync(cancellationToken);
        if (result.IsSuccess && result.Value is not null)
        {
            return Ok(result.Value);
        }

        return FromError(result.Error);
    }

    [HttpPost("{id:guid}/approve")]
    [ProducesResponseType(typeof(ModerateListingResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ModerateListingResponse>> Approve(
        Guid id,
        CancellationToken cancellationToken)
    {
        var result = await _adminListingsService.ApproveAsync(id, cancellationToken);
        if (result.IsSuccess && result.Value is not null)
        {
            return Ok(result.Value);
        }

        return FromError(result.Error);
    }

    [HttpPost("{id:guid}/reject")]
    [ProducesResponseType(typeof(ModerateListingResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ModerateListingResponse>> Reject(
        Guid id,
        [FromBody] RejectListingRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _adminListingsService.RejectAsync(id, request.ReasonCode, request.Note, cancellationToken);
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
            "admin.listing_not_found" => NotFound(error.ToProblemDetails(StatusCodes.Status404NotFound)),
            "admin.invalid_listing_status" => Conflict(error.ToProblemDetails(StatusCodes.Status409Conflict)),
            _ => BadRequest(error.ToProblemDetails(StatusCodes.Status400BadRequest))
        };
    }
}
