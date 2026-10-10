namespace RentalPlatform.Domain.Enums;

// Stored as an int in UserTokens.Purpose. Values are explicit and never reused or renumbered
// (ADR-028 §11): a retired purpose keeps its number forever.
public enum TokenPurpose
{
    EmailVerification = 1
}
