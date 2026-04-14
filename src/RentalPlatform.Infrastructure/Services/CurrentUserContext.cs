using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using RentalPlatform.Application.Abstractions;
using System.IdentityModel.Tokens.Jwt;

namespace RentalPlatform.Infrastructure.Services;

public sealed class CurrentUserContext : ICurrentUserContext
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public CurrentUserContext(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public Guid? UserId
    {
        get
        {
            var user = _httpContextAccessor.HttpContext?.User;
            var userIdClaim =
                user?.FindFirstValue(ClaimTypes.NameIdentifier) ??
                user?.FindFirstValue(JwtRegisteredClaimNames.Sub);

            return Guid.TryParse(userIdClaim, out var userId) ? userId : null;
        }
    }
}
