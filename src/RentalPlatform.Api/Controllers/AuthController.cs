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
    private readonly IExternalNonceService _externalNonceService;

    public AuthController(IAuthService authService, IExternalNonceService externalNonceService)
    {
        _authService = authService;
        _externalNonceService = externalNonceService;
    }

    [HttpPost("register")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimiterExtensions.AuthPolicy)]
    [ProducesResponseType(typeof(RegisterResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<RegisterResponse>> Register(
        [FromBody] RegisterRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _authService.RegisterAsync(request, cancellationToken);
        if (result.IsSuccess && result.Value is not null)
        {
            // No token: the account is a pending registration until the email is verified (ADR-028).
            return StatusCode(StatusCodes.Status201Created, result.Value);
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

    [HttpPost("verify-email")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimiterExtensions.EmailVerificationPolicy)]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<AuthResponse>> VerifyEmail(
        [FromBody] VerifyEmailRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _authService.VerifyEmailAsync(request, cancellationToken);
        if (result.IsSuccess && result.Value is not null)
        {
            return Ok(result.Value);
        }

        return FromError(result.Error);
    }

    // Always 202 with no body, whether or not the address is registered (no enumeration through
    // this endpoint). The only other answer is 503 while the production email gate is closed.
    [HttpPost("resend-verification")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimiterExtensions.EmailVerificationPolicy)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult> ResendVerification(
        [FromBody] ResendVerificationRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _authService.ResendVerificationAsync(request, cancellationToken);
        if (result.IsSuccess)
        {
            return Accepted();
        }

        return FromError(result.Error);
    }

    // The single-use nonce the SPA passes to Google Identity Services (ADR-030 section 2). 503 when
    // Google is not configured or the nonce store is full.
    [HttpPost("external/nonce")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimiterExtensions.ExternalAuthNoncePolicy)]
    [ProducesResponseType(typeof(ExternalNonceResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public ActionResult<ExternalNonceResponse> ExternalNonce()
    {
        var result = _externalNonceService.Issue();
        if (result.IsSuccess && result.Value is not null)
        {
            return Ok(result.Value);
        }

        return FromError(result.Error);
    }

    [HttpPost("external")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimiterExtensions.ExternalAuthPolicy)]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
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

    [HttpPut("me/name")]
    [Authorize]
    [EnableRateLimiting(RateLimiterExtensions.NameUpdatePolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult> UpdateName(
        [FromBody] UpdateNameRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _authService.UpdateNameAsync(request.FirstName, request.LastName, cancellationToken);
        if (result.IsSuccess)
        {
            return NoContent();
        }

        return FromError(result.Error);
    }

    [HttpPut("me/phone")]
    [Authorize]
    [EnableRateLimiting(RateLimiterExtensions.PhoneUpdatePolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult> UpdatePhone(
        [FromBody] UpdatePhoneRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _authService.UpdatePhoneAsync(request.PhoneNumber, cancellationToken);
        if (result.IsSuccess)
        {
            return NoContent();
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

    private ObjectResult CooldownResponse(ServiceError error)
    {
        if (error.RetryAfterSeconds is { } seconds)
        {
            Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return StatusCode(StatusCodes.Status429TooManyRequests, error.ToProblemDetails(StatusCodes.Status429TooManyRequests));
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
            "auth.email_not_verified" => StatusCode(StatusCodes.Status403Forbidden, error.ToProblemDetails(StatusCodes.Status403Forbidden)),
            "auth.verification_token_invalid" => BadRequest(error.ToProblemDetails(StatusCodes.Status400BadRequest)),
            "auth.verification_token_expired" => BadRequest(error.ToProblemDetails(StatusCodes.Status400BadRequest)),
            "auth.email_already_verified" => Conflict(error.ToProblemDetails(StatusCodes.Status409Conflict)),
            "auth.registration_unavailable" => StatusCode(StatusCodes.Status503ServiceUnavailable, error.ToProblemDetails(StatusCodes.Status503ServiceUnavailable)),
            "auth.verification_cooldown" => CooldownResponse(error),
            "auth.user_blocked" => StatusCode(StatusCodes.Status403Forbidden, error.ToProblemDetails(StatusCodes.Status403Forbidden)),
            "auth.external_link_conflict" => Conflict(error.ToProblemDetails(StatusCodes.Status409Conflict)),
            "auth.external_pending_registration" => Conflict(error.ToProblemDetails(StatusCodes.Status409Conflict)),
            "auth.external_provider_unavailable" => StatusCode(StatusCodes.Status503ServiceUnavailable, error.ToProblemDetails(StatusCodes.Status503ServiceUnavailable)),
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
