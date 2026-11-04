using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Tests.Api;

// Generates signed JWT tokens for integration tests using the same key/issuer/audience
// that RentalPlatformWebAppFactory injects into the test host's configuration.
public static class TestJwtTokenHelper
{
    public static string GenerateToken(
        Guid userId, string email, UserRole role = UserRole.User, int? tokenVersion = 0)
    {
        // tokenVersion 0 matches a freshly seeded user (Users.TokenVersion default); null omits the claim,
        // like a token issued before ADR-055.
        var claims = new List<Claim>
        {
            new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, email),
            new Claim(ClaimTypes.Email, email),
            new Claim(ClaimTypes.Role, role.ToString()),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };
        if (tokenVersion is { } version)
        {
            claims.Add(new Claim("tv", version.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(RentalPlatformWebAppFactory.JwtSecretKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: RentalPlatformWebAppFactory.JwtIssuer,
            audience: RentalPlatformWebAppFactory.JwtAudience,
            claims: claims,
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
