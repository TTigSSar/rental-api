using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Domain.Entities;

// A single-use secret issued to a user for one purpose (today: email verification, ADR-028).
// Only the SHA-256 of the secret is stored; the secret itself exists only in the emailed link.
// A replaced or superseded token is revoked (ConsumedAt set), never deleted.
public sealed class UserToken
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public TokenPurpose Purpose { get; set; }
    public byte[] TokenHash { get; set; } = Array.Empty<byte>();
    public DateTime ExpiresAt { get; set; }
    public DateTime? ConsumedAt { get; set; }
    public DateTime CreatedAt { get; set; }

    public User User { get; set; } = null!;
}
