using System.IdentityModel.Tokens.Jwt;
using System.Globalization;
using System.Security.Claims;

namespace RentalPlatform.Infrastructure.Services;

/// <summary>
/// The ADR-055 acceptance rule for an already signature-validated token, shared by the JWT bearer
/// pipeline and the chat hub's connect-time re-check so the two can never disagree.
/// </summary>
public static class SessionTokenValidator
{
    /// <summary>
    /// True only when the user exists, is not blocked and the token's <c>tv</c> equals the stored TokenVersion.
    /// A token without a parseable <c>tv</c> (issued before ADR-055) is rejected. Throws if the state read fails.
    /// </summary>
    public static async Task<bool> IsAcceptedAsync(
        ClaimsPrincipal principal, UserSecurityStateCache cache, CancellationToken cancellationToken)
    {
        // Same parsing as CurrentUserContext.
        var userIdClaim =
            principal.FindFirstValue(ClaimTypes.NameIdentifier) ??
            principal.FindFirstValue(JwtRegisteredClaimNames.Sub);
        if (!Guid.TryParse(userIdClaim, out var userId))
        {
            return false;
        }

        if (!int.TryParse(
                principal.FindFirstValue(JwtTokenService.TokenVersionClaim),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var tokenVersion))
        {
            return false;
        }

        var state = await cache.GetAsync(userId, cancellationToken);
        return state is { IsBlocked: false } current && current.TokenVersion == tokenVersion;
    }
}
