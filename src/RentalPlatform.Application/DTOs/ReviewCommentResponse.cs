namespace RentalPlatform.Application.DTOs;

/// <summary>
/// A public review comment card. Deliberately carries NO individual scores —
/// scores are private and surface only as aggregates.
/// </summary>
public sealed class ReviewCommentResponse
{
    public Guid Id { get; init; }
    public string ReviewerFirstName { get; init; } = string.Empty;
    public string ReviewerLastName { get; init; } = string.Empty;
    public string? ReviewerAvatarUrl { get; init; }
    // ADR-056: the reviewer deleted their account (names above are then empty, avatar null).
    public bool ReviewerIsDeleted { get; init; }
    public string Comment { get; init; } = string.Empty;
    public int RentedDays { get; init; }
    public DateTime CreatedAt { get; init; }
}
