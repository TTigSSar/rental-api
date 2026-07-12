using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RentalPlatform.Api.Extensions;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.Common;
using RentalPlatform.Application.DTOs;

namespace RentalPlatform.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class ReviewsController : ControllerBase
{
    private readonly IReviewsService _reviewsService;

    public ReviewsController(IReviewsService reviewsService)
    {
        _reviewsService = reviewsService;
    }

    [HttpPost("toy")]
    [Authorize]
    [ProducesResponseType(typeof(BookingReviewStatusResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<BookingReviewStatusResponse>> SubmitToy(
        [FromBody] CreateToyReviewRequest request, CancellationToken cancellationToken)
    {
        var result = await _reviewsService.SubmitToyReviewAsync(request, cancellationToken);
        return result.IsSuccess && result.Value is not null
            ? StatusCode(StatusCodes.Status201Created, result.Value)
            : FromError(result.Error);
    }

    [HttpPost("owner")]
    [Authorize]
    [ProducesResponseType(typeof(BookingReviewStatusResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<BookingReviewStatusResponse>> SubmitOwner(
        [FromBody] CreateOwnerReviewRequest request, CancellationToken cancellationToken)
    {
        var result = await _reviewsService.SubmitOwnerReviewAsync(request, cancellationToken);
        return result.IsSuccess && result.Value is not null
            ? StatusCode(StatusCodes.Status201Created, result.Value)
            : FromError(result.Error);
    }

    [HttpPost("renter")]
    [Authorize]
    [ProducesResponseType(typeof(BookingReviewStatusResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<BookingReviewStatusResponse>> SubmitRenter(
        [FromBody] CreateRenterReviewRequest request, CancellationToken cancellationToken)
    {
        var result = await _reviewsService.SubmitRenterReviewAsync(request, cancellationToken);
        return result.IsSuccess && result.Value is not null
            ? StatusCode(StatusCodes.Status201Created, result.Value)
            : FromError(result.Error);
    }

    [HttpGet("booking/{bookingId:guid}/status")]
    [Authorize]
    [ProducesResponseType(typeof(BookingReviewStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BookingReviewStatusResponse>> GetBookingStatus(
        Guid bookingId, CancellationToken cancellationToken)
    {
        var result = await _reviewsService.GetBookingReviewStatusAsync(bookingId, cancellationToken);
        return result.IsSuccess && result.Value is not null ? Ok(result.Value) : FromError(result.Error);
    }

    [HttpGet("listing/{listingId:guid}")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ToyReviewSummaryResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<ToyReviewSummaryResponse>> GetListingToyReviews(
        Guid listingId, CancellationToken cancellationToken)
    {
        var summary = await _reviewsService.GetListingToyReviewsAsync(listingId, cancellationToken);
        return Ok(summary);
    }

    [HttpGet("owner/{userId:guid}")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(OwnerReviewSummaryResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<OwnerReviewSummaryResponse>> GetOwnerReviews(
        Guid userId, CancellationToken cancellationToken)
    {
        var summary = await _reviewsService.GetOwnerReviewsAsync(userId, cancellationToken);
        return Ok(summary);
    }

    [HttpGet("renter/{userId:guid}")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(RenterReviewSummaryResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<RenterReviewSummaryResponse>> GetRenterReviews(
        Guid userId, CancellationToken cancellationToken)
    {
        var summary = await _reviewsService.GetRenterReviewsAsync(userId, cancellationToken);
        return Ok(summary);
    }

    private ActionResult FromError(ServiceError? error)
    {
        if (error is null)
        {
            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "Unexpected error.");
        }

        return error.Code switch
        {
            "review.unauthenticated"       => Unauthorized(error.ToProblemDetails(StatusCodes.Status401Unauthorized)),
            "review.booking_not_found"     => NotFound(error.ToProblemDetails(StatusCodes.Status404NotFound)),
            "review.forbidden"             => StatusCode(StatusCodes.Status403Forbidden, error.ToProblemDetails(StatusCodes.Status403Forbidden)),
            "review.booking_not_completed" => Conflict(error.ToProblemDetails(StatusCodes.Status409Conflict)),
            "review.already_submitted"     => Conflict(error.ToProblemDetails(StatusCodes.Status409Conflict)),
            _ => BadRequest(error.ToProblemDetails(StatusCodes.Status400BadRequest))
        };
    }
}
