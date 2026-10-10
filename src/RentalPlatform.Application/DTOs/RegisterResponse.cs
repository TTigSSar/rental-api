namespace RentalPlatform.Application.DTOs;

// Register no longer signs anyone in (ADR-028 §1): the account is a pending registration until the
// mailbox is proven, so there is no token and no user object here.
public sealed class RegisterResponse
{
    public string Email { get; init; } = string.Empty;
    public bool VerificationRequired { get; init; } = true;
}
