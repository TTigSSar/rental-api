using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using RentalPlatform.Api.Controllers.Requests;
using RentalPlatform.Api.Extensions;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.DTOs;

namespace RentalPlatform.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[AllowAnonymous]
public sealed class DistrictsController : ControllerBase
{
    private readonly IDistrictsQueryService _districtsQueryService;

    public DistrictsController(IDistrictsQueryService districtsQueryService)
    {
        _districtsQueryService = districtsQueryService;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyCollection<ListingDistrictResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyCollection<ListingDistrictResponse>>> GetAll(CancellationToken cancellationToken)
    {
        var districts = await _districtsQueryService.GetAllAsync(cancellationToken);
        return Ok(districts);
    }

    /// <summary>
    /// Which Yerevan district a coordinate falls in, plus whether it is inside the Armenia
    /// bounding box home points are accepted in.
    /// </summary>
    /// <remarks>
    /// Deliberately AllowAnonymous: the sign-up wizard shows the district live under the pin,
    /// before the account it belongs to exists, so there is no token to require. Nothing here is
    /// private — the caller supplies the coordinate and the boundaries are public OSM data resolved
    /// locally. A point outside Yerevan is a 200 with a null district, not an error; only a
    /// malformed coordinate (outside WGS84 range) is a 400.
    /// </remarks>
    [HttpGet("at")]
    [EnableRateLimiting(RateLimiterExtensions.DistrictLookupPolicy)]
    [ProducesResponseType(typeof(DistrictAtResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<DistrictAtResponse>> GetAt(
        [FromQuery] DistrictAtQuery query,
        CancellationToken cancellationToken)
    {
        // Non-null by [Required] on both properties — model validation has already run.
        var result = await _districtsQueryService.FindAtAsync(
            query.Lat!.Value,
            query.Lng!.Value,
            cancellationToken);

        return Ok(result);
    }
}
