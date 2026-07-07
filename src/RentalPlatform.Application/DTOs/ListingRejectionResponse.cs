namespace RentalPlatform.Application.DTOs;

// Structured rejection detail surfaced to the owner so the UI can render a localized reason
// chip plus the moderator's free-text note (instead of a single opaque string).
public sealed class ListingRejectionResponse
{
    public string ReasonCode { get; init; } = string.Empty;
    public string ReasonLabel { get; init; } = string.Empty;
    public string? Note { get; init; }
    public string? ModeratorName { get; init; }
    public DateTime? ModeratedAt { get; init; }
}
