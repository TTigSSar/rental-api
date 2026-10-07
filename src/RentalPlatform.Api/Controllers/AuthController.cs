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
public sealed class AuthController : ControllerBase
{
    private readonly IAuthService _authService;

    public AuthController(IAuthService authService)
    {
        _authService = authService;
    }

    [HttpPost("register")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimiterExtensions.AuthPolicy)]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<AuthResponse>> Register(
        [FromBody] RegisterRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _authService.RegisterAsync(request, cancellationToken);
        if (result.IsSuccess && result.Value is not null)
        {
            return Ok(result.Value);
        }

        return FromError(result.Error);
    }

    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimiterExtensions.AuthPolicy)]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<AuthResponse>> Login(
        [FromBody] LoginRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _authService.LoginAsync(request, cancellationToken);
        if (result.IsSuccess && result.Value is not null)
        {
            return Ok(result.Value);
        }

        return FromError(result.Error);
    }

    [HttpPost("external")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AuthResponse>> External(
        [FromBody] ExternalAuthRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _authService.ExternalAsync(request, cancellationToken);
        if (result.IsSuccess && result.Value is not null)
        {
            return Ok(result.Value);
        }

        return FromError(result.Error);
    }

    [HttpGet("me")]
    [Authorize]
    [ProducesResponseType(typeof(CurrentUserResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<CurrentUserResponse>> Me(CancellationToken cancellationToken)
    {
        var result = await _authService.GetCurrentUserAsync(cancellationToken);
        if (result.IsSuccess && result.Value is not null)
        {
            return Ok(result.Value);
        }

        return FromError(result.Error);
    }

    [HttpPut("me/preferred-language")]
    [Authorize]
    [ProducesResponseType(typeof(CurrentUserResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<CurrentUserResponse>> UpdatePreferredLanguage(
        [FromBody] UpdatePreferredLanguageRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _authService.UpdatePreferredLanguageAsync(request.PreferredLanguage, cancellationToken);
        if (result.IsSuccess && result.Value is not null)
        {
            return Ok(result.Value);
        }

        return FromError(result.Error);
    }

    [HttpPut("me/password")]
    [Authorize]
    [EnableRateLimiting(RateLimiterExtensions.PasswordChangePolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult> ChangePassword(
        [FromBody] ChangePasswordRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _authService.ChangePasswordAsync(request.CurrentPassword, request.NewPassword, cancellationToken);
        if (result.IsSuccess)
        {
            return NoContent();
        }

        return FromError(result.Error);
    }

    // The home point is the single source of every listing this user owns (home-point model), so
    // saving one rewrites all of them. Per-account rate limited; the exact coordinates come back
    // only on CurrentUserResponse, which only ever goes to the account that owns them.
    [HttpPut("me/home-point")]
    [Authorize]
    [EnableRateLimiting(RateLimiterExtensions.HomePointPolicy)]
    [ProducesResponseType(typeof(CurrentUserResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<CurrentUserResponse>> UpdateHomePoint(
        [FromBody] UpdateHomePointRequest request,
        CancellationToken cancellationToken)
    {
        // Non-null by [Required] on both properties — model validation has already run.
        var result = await _authService.UpdateHomePointAsync(
            request.Latitude!.Value,
            request.Longitude!.Value,
            cancellationToken);

        if (result.IsSuccess && result.Value is not null)
        {
            return Ok(result.Value);
        }

        return FromError(result.Error);
    }

    // Only available while the user owns no listings at all — a listing with no location would be
    // unplaceable on the map, so an owner must remove their listings first (409 auth.home_point_in_use).
    [HttpDelete("me/home-point")]
    [Authorize]
    [EnableRateLimiting(RateLimiterExtensions.HomePointPolicy)]
    [ProducesResponseType(typeof(CurrentUserResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<CurrentUserResponse>> ClearHomePoint(CancellationToken cancellationToken)
    {
        var result = await _authService.ClearHomePointAsync(cancellationToken);
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
            "auth.duplicate_email" => Conflict(error.ToProblemDetails(StatusCodes.Status409Conflict)),
            "auth.invalid_credentials" => Unauthorized(error.ToProblemDetails(StatusCodes.Status401Unauthorized)),
            "auth.unauthenticated" => Unauthorized(error.ToProblemDetails(StatusCodes.Status401Unauthorized)),
            "auth.user_blocked" => StatusCode(StatusCodes.Status403Forbidden, error.ToProblemDetails(StatusCodes.Status403Forbidden)),
            "auth.external_link_conflict" => Conflict(error.ToProblemDetails(StatusCodes.Status409Conflict)),
            "auth.invalid_current_password" => BadRequest(error.ToProblemDetails(StatusCodes.Status400BadRequest)),
            "auth.password_not_set" => BadRequest(error.ToProblemDetails(StatusCodes.Status400BadRequest)),
            "auth.password_unchanged" => BadRequest(error.ToProblemDetails(StatusCodes.Status400BadRequest)),
            "auth.password_too_long" => BadRequest(error.ToProblemDetails(StatusCodes.Status400BadRequest)),
            "auth.home_point_in_use" => Conflict(error.ToProblemDetails(StatusCodes.Status409Conflict)),
            // 400: the pin is simply in the wrong place and the user can move it. Reached from both
            // POST /register (optional home-point step) and PUT /me/home-point.
            "auth.home_point_outside_yerevan" => BadRequest(error.ToProblemDetails(StatusCodes.Status400BadRequest)),
            _ => BadRequest(error.ToProblemDetails(StatusCodes.Status400BadRequest))
        };
    }
}
