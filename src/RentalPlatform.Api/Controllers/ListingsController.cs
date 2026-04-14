using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.DTOs;

namespace RentalPlatform.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[AllowAnonymous]
public sealed class ListingsController : ControllerBase
{
    private readonly IListingsQueryService _listingsQueryService;

    public ListingsController(IListingsQueryService listingsQueryService)
    {
        _listingsQueryService = listingsQueryService;
    }

    [HttpGet]
    [ProducesResponseType(typeof(PagedResult<ListingPreviewResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<ListingPreviewResponse>>> GetListings(
        [FromQuery] ListingsQueryFilter filter,
        CancellationToken cancellationToken)
    {
        var result = await _listingsQueryService.GetApprovedListingsAsync(filter, cancellationToken);
        return Ok(result);
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(ListingDetailsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ListingDetailsResponse>> GetListingById(Guid id, CancellationToken cancellationToken)
    {
        var result = await _listingsQueryService.GetApprovedListingByIdAsync(id, cancellationToken);
        if (result is null)
        {
            return NotFound();
        }

        return Ok(result);
    }
}
