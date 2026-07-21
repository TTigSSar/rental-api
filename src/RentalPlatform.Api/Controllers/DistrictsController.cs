using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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
}
