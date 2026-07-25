using RentalPlatform.Application.Abstractions;
using RentalPlatform.Domain.Entities;

namespace RentalPlatform.Tests.TestSupport;

// Minimal test double for IJwtTokenService — returns a fixed marker token, no real signing.
public sealed class FakeJwtTokenService : IJwtTokenService
{
    public string GenerateAccessToken(User user) => $"fake-token:{user.Id}";
}
