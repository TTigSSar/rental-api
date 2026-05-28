using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.DTOs;

namespace RentalPlatform.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class UsersController : ControllerBase
{
    private readonly IPublicUserProfileService _profileService;

    public UsersController(IPublicUserProfileService profileService)
    {
        _profileService = profileService;
    }

    [HttpGet("{userId:guid}/public-profile")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(PublicUserProfileResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PublicUserProfileResponse>> GetPublicProfile(
        Guid userId,
        CancellationToken cancellationToken)
    {
        var profile = await _profileService.GetPublicProfileAsync(userId, cancellationToken);
        if (profile is null) return NotFound();
        return Ok(profile);
    }
}
