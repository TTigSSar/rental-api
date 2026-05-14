using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.DTOs;

namespace RentalPlatform.Api.Controllers;

[ApiController]
[Route("api/home")]
public sealed class HomeController : ControllerBase
{
    private readonly IHomeSectionsService _homeSectionsService;

    public HomeController(IHomeSectionsService homeSectionsService)
    {
        _homeSectionsService = homeSectionsService;
    }

    [HttpGet("sections")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(HomeSectionsResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<HomeSectionsResponse>> GetSections(
        [FromQuery] int itemsPerSection = 6,
        CancellationToken cancellationToken = default)
    {
        var result = await _homeSectionsService.GetSectionsAsync(itemsPerSection, cancellationToken);
        return Ok(result);
    }
}
