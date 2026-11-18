using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RentalPlatform.Api.Extensions;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.Common;
using RentalPlatform.Application.DTOs;

namespace RentalPlatform.Api.Controllers;

// Web Push subscriptions (ADR-067 sections 7 and 8).
[ApiController]
[Route("api/push")]
public sealed class PushController : ControllerBase
{
    private readonly IPushSubscriptionService _pushSubscriptionService;

    public PushController(IPushSubscriptionService pushSubscriptionService)
    {
        _pushSubscriptionService = pushSubscriptionService;
    }

    [HttpGet("public-key")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(PushPublicKeyResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public IActionResult GetPublicKey()
    {
        var result = _pushSubscriptionService.GetPublicKey();
        return result.IsSuccess ? Ok(result.Value) : FromError(result.Error);
    }

    [HttpPut("subscription")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Subscribe([FromBody] PushSubscriptionRequest request, CancellationToken cancellationToken)
    {
        var result = await _pushSubscriptionService.SubscribeAsync(request, cancellationToken);
        return result.IsSuccess ? NoContent() : FromError(result.Error);
    }

    // DELETE with a JSON body: the endpoint URL is too long and too sensitive for a path or query string.
    [HttpDelete("subscription")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Unsubscribe([FromBody] PushUnsubscribeRequest request, CancellationToken cancellationToken)
    {
        var result = await _pushSubscriptionService.UnsubscribeAsync(request, cancellationToken);
        return result.IsSuccess ? NoContent() : FromError(result.Error);
    }

    private IActionResult FromError(ServiceError? error)
    {
        if (error is null)
        {
            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "Unexpected error.");
        }

        return error.Code switch
        {
            "push.unauthenticated" => Unauthorized(error.ToProblemDetails(StatusCodes.Status401Unauthorized)),
            "push.user_blocked" => StatusCode(StatusCodes.Status403Forbidden, error.ToProblemDetails(StatusCodes.Status403Forbidden)),
            "push.not_configured" => NotFound(error.ToProblemDetails(StatusCodes.Status404NotFound)),
            _ => BadRequest(error.ToProblemDetails(StatusCodes.Status400BadRequest))
        };
    }
}
