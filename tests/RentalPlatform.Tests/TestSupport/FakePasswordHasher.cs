using RentalPlatform.Application.Abstractions;

namespace RentalPlatform.Tests.TestSupport;

// Minimal test double for IPasswordHasher. Not cryptographically meaningful — only used
// where AuthService needs a hasher instance but the test doesn't exercise password logic.
public sealed class FakePasswordHasher : IPasswordHasher
{
    public string HashPassword(string password) => $"hashed:{password}";

    public bool VerifyPassword(string password, string passwordHash) => passwordHash == $"hashed:{password}";
}
