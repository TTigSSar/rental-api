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

    [HttpPost("{id:guid}/return/mark")]
    [ProducesResponseType(typeof(BookingDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<BookingDetailResponse>> MarkReturned(Guid id, CancellationToken cancellationToken)
    {
        var result = await _bookingsService.MarkReturnedAsync(id, cancellationToken);
        if (result.IsSuccess && result.Value is not null)
        {
            return Ok(result.Value);
        }

        return FromError(result.Error);
    }

    [HttpPost("{id:guid}/return/confirm")]
    [ProducesResponseType(typeof(BookingDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<BookingDetailResponse>> ConfirmReturn(Guid id, CancellationToken cancellationToken)
    {
        var result = await _bookingsService.ConfirmReturnAsync(id, cancellationToken);
        if (result.IsSuccess && result.Value is not null)
        {
            return Ok(result.Value);
        }

        return FromError(result.Error);
    }

    [HttpPost("{id:guid}/return/undo")]
    [ProducesResponseType(typeof(BookingDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<BookingDetailResponse>> UndoReturn(Guid id, CancellationToken cancellationToken)
    {
        var result = await _bookingsService.UndoReturnAsync(id, cancellationToken);
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
            "booking.unauthenticated" => Unauthorized(ToProblemDetails(StatusCodes.Status401Unauthorized, error)),
            "booking.user_blocked" => StatusCode(StatusCodes.Status403Forbidden, ToProblemDetails(StatusCodes.Status403Forbidden, error)),
            "booking.own_listing_forbidden" => StatusCode(StatusCodes.Status403Forbidden, ToProblemDetails(StatusCodes.Status403Forbidden, error)),
            "booking.forbidden" => StatusCode(StatusCodes.Status403Forbidden, ToProblemDetails(StatusCodes.Status403Forbidden, error)),
            "booking.self_confirm_forbidden" => StatusCode(StatusCodes.Status403Forbidden, ToProblemDetails(StatusCodes.Status403Forbidden, error)),
            "booking.listing_not_found" => NotFound(ToProblemDetails(StatusCodes.Status404NotFound, error)),
            "booking.not_found" => NotFound(ToProblemDetails(StatusCodes.Status404NotFound, error)),
            "booking.overlap" => Conflict(ToProblemDetails(StatusCodes.Status409Conflict, error)),
            "booking.not_pending" => Conflict(ToProblemDetails(StatusCodes.Status409Conflict, error)),
            "booking.not_cancellable" => Conflict(ToProblemDetails(StatusCodes.Status409Conflict, error)),
            "booking.not_markable" => Conflict(ToProblemDetails(StatusCodes.Status409Conflict, error)),
            "booking.not_confirmable" => Conflict(ToProblemDetails(StatusCodes.Status409Conflict, error)),
            "booking.not_undoable" => Conflict(ToProblemDetails(StatusCodes.Status409Conflict, error)),
            "booking.listing_not_approved" => BadRequest(ToProblemDetails(StatusCodes.Status400BadRequest, error)),
            "booking.invalid_dates" => BadRequest(ToProblemDetails(StatusCodes.Status400BadRequest, error)),
            _ => BadRequest(ToProblemDetails(StatusCodes.Status400BadRequest, error))
        };
    }

    private static ProblemDetails ToProblemDetails(int statusCode, ServiceError error) => new()
    {
        Status = statusCode,
        Title = error.Message,
        Type = error.Code
    };
}
