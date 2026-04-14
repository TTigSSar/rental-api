using RentalPlatform.Domain.Entities;

namespace RentalPlatform.Application.Abstractions;

public interface IJwtTokenService
{
    string GenerateAccessToken(User user);
}
