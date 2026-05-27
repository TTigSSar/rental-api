using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using RentalPlatform.Api.Controllers.Requests;
using RentalPlatform.Api.Extensions;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.Common;
using RentalPlatform.Application.DTOs;
using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class ListingsController : ControllerBase
{
    // 25 MB cap on the multipart upload body. Sized for ~10 images x ~2.5 MB each
    // with room for multipart boundaries; coordinated with MaxImagesPerUpload in the service layer.
    private const long MaxUploadBytes = 25L * 1024 * 1024;

    private readonly IListingsQueryService _listingsQueryService;
    private readonly IListingsOwnerService _listingsOwnerService;
    private readonly IListingImagesOwnerService _listingImagesOwnerService;

    public ListingsController(
        IListingsQueryService listingsQueryService,
        IListingsOwnerService listingsOwnerService,
        IListingImagesOwnerService listingImagesOwnerService)
    {
        _listingsQueryService = listingsQueryService;
        _listingsOwnerService = listingsOwnerService;
        _listingImagesOwnerService = listingImagesOwnerService;
    }

    [HttpGet]
    [AllowAnonymous]
    [ProducesResponseType(typeof(PagedResult<ListingPreviewResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<ListingPreviewResponse>>> GetListings(
        [FromQuery] ListingsQueryFilter filter,
        CancellationToken cancellationToken)
    {
        var result = await _listingsQueryService.GetApprovedListingsAsync(filter, cancellationToken);
        return Ok(result);
    }

    [HttpGet("{id:guid}")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ListingDetailsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ListingDetailsResponse>> GetListingById(Guid id, CancellationToken cancellationToken)
    {
        Guid? callerId = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var parsedId)
            ? parsedId
            : null;

        bool isAdmin = User.IsInRole(nameof(UserRole.Admin));
        var result = await _listingsQueryService.GetApprovedListingByIdAsync(id, callerId, isAdmin, cancellationToken);
        if (result is null)
        {
            return NotFound();
        }

        return Ok(result);
    }

    [HttpPost]
    [Authorize]
    [ProducesResponseType(typeof(CreateListingResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<CreateListingResponse>> Create(
        [FromBody] CreateListingRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _listingsOwnerService.CreateAsync(request, cancellationToken);
        if (result.IsSuccess && result.Value is not null)
        {
            return StatusCode(StatusCodes.Status201Created, result.Value);
        }

        return FromOwnerError(result.Error);
    }

    [HttpGet("mine")]
    [Authorize]
    [ProducesResponseType(typeof(IReadOnlyCollection<MyListingResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyCollection<MyListingResponse>>> GetMine(CancellationToken cancellationToken)
    {
        var result = await _listingsOwnerService.GetMineAsync(cancellationToken);
        if (result.IsSuccess && result.Value is not null)
        {
            return Ok(result.Value);
        }

        return FromOwnerError(result.Error);
    }

    [HttpPost("{listingId:guid}/images")]
    [Authorize]
    [EnableRateLimiting(RateLimiterExtensions.ImageUploadPolicy)]
    [RequestSizeLimit(MaxUploadBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxUploadBytes)]
    [ProducesResponseType(typeof(IReadOnlyCollection<ListingImageResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<IReadOnlyCollection<ListingImageResponse>>> UploadImages(
        Guid listingId,
        [FromForm] UploadListingImagesRequest request,
        CancellationToken cancellationToken)
    {
        var openStreams = new List<Stream>();

        try
        {
            var uploadFiles = request.Files
                .Select(file =>
                {
                    var stream = file.OpenReadStream();
                    openStreams.Add(stream);

                    return new UploadListingImageRequest
                    {
                        FileName = file.FileName,
                        ContentType = file.ContentType,
                        Length = file.Length,
                        Content = stream
                    };
                })
                .ToList();

            var result = await _listingImagesOwnerService.UploadAsync(listingId, uploadFiles, cancellationToken);
            if (result.IsSuccess && result.Value is not null)
            {
                return Ok(result.Value);
            }

            return FromOwnerError(result.Error);
        }
        finally
        {
            foreach (var stream in openStreams)
            {
                await stream.DisposeAsync();
            }
        }
    }

    [HttpPost("{id:guid}/archive")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Archive(Guid id, CancellationToken cancellationToken)
    {
        var result = await _listingsOwnerService.ArchiveAsync(id, cancellationToken);
        if (result.IsSuccess)
        {
            return NoContent();
        }

        return FromOwnerError(result.Error);
    }

    [HttpPost("{id:guid}/restore")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Restore(Guid id, CancellationToken cancellationToken)
    {
        var result = await _listingsOwnerService.RestoreAsync(id, cancellationToken);
        if (result.IsSuccess)
        {
            return NoContent();
        }

        return FromOwnerError(result.Error);
    }

    [HttpPatch("{id:guid}")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(
        Guid id,
        [FromBody] UpdateListingRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _listingsOwnerService.UpdateAsync(id, request, cancellationToken);
        if (result.IsSuccess)
        {
            return NoContent();
        }

        return FromOwnerError(result.Error);
    }

    [HttpDelete("{listingId:guid}/images/{imageId:guid}")]
    [Authorize]
    [ProducesResponseType(typeof(IReadOnlyCollection<ListingImageResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyCollection<ListingImageResponse>>> DeleteImage(
        Guid listingId,
        Guid imageId,
        CancellationToken cancellationToken)
    {
        var result = await _listingImagesOwnerService.DeleteAsync(listingId, imageId, cancellationToken);
        if (result.IsSuccess && result.Value is not null)
        {
            return Ok(result.Value);
        }

        return FromOwnerError(result.Error);
    }

    private ActionResult FromOwnerError(ServiceError? error)
    {
        if (error is null)
        {
            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "Unexpected error.");
        }

        return error.Code switch
        {
            "listing.unauthenticated" => Unauthorized(ToProblemDetails(StatusCodes.Status401Unauthorized, error)),
            "listing.user_blocked" => StatusCode(StatusCodes.Status403Forbidden, ToProblemDetails(StatusCodes.Status403Forbidden, error)),
            "listing.forbidden" => StatusCode(StatusCodes.Status403Forbidden, ToProblemDetails(StatusCodes.Status403Forbidden, error)),
            "listing.not_found" => NotFound(ToProblemDetails(StatusCodes.Status404NotFound, error)),
            "listing.category_not_found" => BadRequest(ToProblemDetails(StatusCodes.Status400BadRequest, error)),
            "listing.invalid_age_range" => BadRequest(ToProblemDetails(StatusCodes.Status400BadRequest, error)),
            "listing.image_empty" => BadRequest(ToProblemDetails(StatusCodes.Status400BadRequest, error)),
            "listing.image_invalid_type" => BadRequest(ToProblemDetails(StatusCodes.Status400BadRequest, error)),
            "listing.image_too_many" => BadRequest(ToProblemDetails(StatusCodes.Status400BadRequest, error)),
            "listing.image_listing_limit" => BadRequest(ToProblemDetails(StatusCodes.Status400BadRequest, error)),
            "listing.image_too_large" => BadRequest(ToProblemDetails(StatusCodes.Status400BadRequest, error)),
            "listing.image_not_found" => NotFound(ToProblemDetails(StatusCodes.Status404NotFound, error)),
            "listing.invalid_status" => BadRequest(ToProblemDetails(StatusCodes.Status400BadRequest, error)),
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
