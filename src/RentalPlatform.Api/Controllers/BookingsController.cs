using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using RentalPlatform.Api.Extensions;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.Common;
using RentalPlatform.Application.DTOs;

namespace RentalPlatform.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public sealed class BookingsController : ControllerBase
{
    private readonly IBookingsService _bookingsService;

    public BookingsController(IBookingsService bookingsService)
    {
        _bookingsService = bookingsService;
    }

    [HttpPost]
    [EnableRateLimiting(RateLimiterExtensions.BookingCreatePolicy)]
    [ProducesResponseType(typeof(BookingResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<BookingResponse>> Create(
        [FromBody] CreateBookingRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _bookingsService.CreateAsync(request, cancellationToken);
        if (result.IsSuccess && result.Value is not null)
        {
            return Ok(result.Value);
        }

        return FromError(result.Error);
    }

    [HttpGet("mine")]
    [ProducesResponseType(typeof(IReadOnlyCollection<BookingResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyCollection<BookingResponse>>> GetMine(CancellationToken cancellationToken)
    {
        var result = await _bookingsService.GetMineAsync(cancellationToken);
        if (result.IsSuccess && result.Value is not null)
        {
            return Ok(result.Value);
        }

        return FromError(result.Error);
    }

    [HttpGet("requests")]
    [ProducesResponseType(typeof(IReadOnlyCollection<BookingRequestResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyCollection<BookingRequestResponse>>> GetRequests(CancellationToken cancellationToken)
    {
        var result = await _bookingsService.GetOwnerRequestsAsync(cancellationToken);
        if (result.IsSuccess && result.Value is not null)
        {
            return Ok(result.Value);
        }

        return FromError(result.Error);
    }

    [HttpPost("{id:guid}/approve")]
    [ProducesResponseType(typeof(BookingRequestResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<BookingRequestResponse>> Approve(Guid id, CancellationToken cancellationToken)
    {
        var result = await _bookingsService.ApproveAsync(id, cancellationToken);
        if (result.IsSuccess && result.Value is not null)
        {
            return Ok(result.Value);
        }

        return FromError(result.Error);
    }

    [HttpPost("{id:guid}/reject")]
    [ProducesResponseType(typeof(BookingRequestResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<BookingRequestResponse>> Reject(
        Guid id,
        [FromBody] RejectBookingRequest? request,
        CancellationToken cancellationToken)
    {
        var result = await _bookingsService.RejectAsync(id, request?.Reason, cancellationToken);
        if (result.IsSuccess && result.Value is not null)
        {
            return Ok(result.Value);
        }

        return FromError(result.Error);
    }

    [HttpPost("{id:guid}/cancel")]
    [ProducesResponseType(typeof(BookingResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<BookingResponse>> Cancel(Guid id, CancellationToken cancellationToken)
    {
        var result = await _bookingsService.CancelAsync(id, cancellationToken);
        if (result.IsSuccess && result.Value is not null)
        {
            return Ok(result.Value);
        }

        return FromError(result.Error);
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(BookingDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BookingDetailResponse>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var result = await _bookingsService.GetByIdAsync(id, cancellationToken);
        if (result.IsSuccess && result.Value is not null)
        {
            return Ok(result.Value);
        }

        return FromError(result.Error);
    }

    [HttpPost("{id:guid}/activate")]
    [ProducesResponseType(typeof(BookingDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<BookingDetailResponse>> MarkActive(Guid id, CancellationToken cancellationToken)
    {
        var result = await _bookingsService.MarkActiveAsync(id, cancellationToken);
        if (result.IsSuccess && result.Value is not null)
        {
            return Ok(result.Value);
        }

        return FromError(result.Error);
    }

    [HttpPost("{id:guid}/complete")]
    [ProducesResponseType(typeof(BookingDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<BookingDetailResponse>> Complete(Guid id, CancellationToken cancellationToken)
    {
        var result = await _bookingsService.CompleteAsync(id, cancellationToken);
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
            "booking.unauthenticated" => Unauthorized(error.ToProblemDetails(StatusCodes.Status401Unauthorized)),
            "booking.user_blocked" => StatusCode(StatusCodes.Status403Forbidden, error.ToProblemDetails(StatusCodes.Status403Forbidden)),
            "booking.own_listing_forbidden" => StatusCode(StatusCodes.Status403Forbidden, error.ToProblemDetails(StatusCodes.Status403Forbidden)),
            "booking.forbidden" => StatusCode(StatusCodes.Status403Forbidden, error.ToProblemDetails(StatusCodes.Status403Forbidden)),
            "booking.listing_not_found" => NotFound(error.ToProblemDetails(StatusCodes.Status404NotFound)),
            "booking.not_found" => NotFound(error.ToProblemDetails(StatusCodes.Status404NotFound)),
            "booking.overlap" => Conflict(error.ToProblemDetails(StatusCodes.Status409Conflict)),
            "booking.not_pending" => Conflict(error.ToProblemDetails(StatusCodes.Status409Conflict)),
            "booking.not_cancellable" => Conflict(error.ToProblemDetails(StatusCodes.Status409Conflict)),
            "booking.not_activatable" => Conflict(error.ToProblemDetails(StatusCodes.Status409Conflict)),
            "booking.not_completable" => Conflict(error.ToProblemDetails(StatusCodes.Status409Conflict)),
            "booking.owner_only" => StatusCode(StatusCodes.Status403Forbidden, error.ToProblemDetails(StatusCodes.Status403Forbidden)),
            "booking.listing_not_approved" => BadRequest(error.ToProblemDetails(StatusCodes.Status400BadRequest)),
            "booking.invalid_dates" => BadRequest(error.ToProblemDetails(StatusCodes.Status400BadRequest)),
            _ => BadRequest(error.ToProblemDetails(StatusCodes.Status400BadRequest))
        };
    }
}
