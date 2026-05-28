using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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

    [HttpPost]
    [Authorize]
    [ProducesResponseType(typeof(ReviewResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ReviewResponse>> Create(
        [FromBody] CreateReviewRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _reviewsService.CreateAsync(request, cancellationToken);
        if (result.IsSuccess && result.Value is not null)
        {
            return StatusCode(StatusCodes.Status201Created, result.Value);
        }

        return FromError(result.Error);
    }

    [HttpGet("listing/{listingId:guid}")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(IReadOnlyCollection<ReviewResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyCollection<ReviewResponse>>> GetByListing(
        Guid listingId,
        CancellationToken cancellationToken)
    {
        var result = await _reviewsService.GetByListingAsync(listingId, cancellationToken);
        return Ok(result.Value);
    }

    [HttpGet("user/{userId:guid}")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(IReadOnlyCollection<ReviewResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyCollection<ReviewResponse>>> GetByUser(
        Guid userId,
        CancellationToken cancellationToken)
    {
        var result = await _reviewsService.GetByUserAsync(userId, cancellationToken);
        return Ok(result.Value);
    }

    [HttpGet("listing/{listingId:guid}/summary")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(RatingSummaryResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<RatingSummaryResponse>> GetListingSummary(
        Guid listingId,
        CancellationToken cancellationToken)
    {
        var summary = await _reviewsService.GetListingSummaryAsync(listingId, cancellationToken);
        return Ok(summary);
    }

    [HttpGet("user/{userId:guid}/summary")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(RatingSummaryResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<RatingSummaryResponse>> GetUserSummary(
        Guid userId,
        CancellationToken cancellationToken)
    {
        var summary = await _reviewsService.GetUserSummaryAsync(userId, cancellationToken);
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
            "review.unauthenticated"     => Unauthorized(ToProblemDetails(StatusCodes.Status401Unauthorized, error)),
            "review.booking_not_found"   => NotFound(ToProblemDetails(StatusCodes.Status404NotFound, error)),
            "review.forbidden"           => StatusCode(StatusCodes.Status403Forbidden, ToProblemDetails(StatusCodes.Status403Forbidden, error)),
            "review.booking_not_completed" => Conflict(ToProblemDetails(StatusCodes.Status409Conflict, error)),
            "review.already_submitted"   => Conflict(ToProblemDetails(StatusCodes.Status409Conflict, error)),
            "review.invalid_rating"      => BadRequest(ToProblemDetails(StatusCodes.Status400BadRequest, error)),
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
