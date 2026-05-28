using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Application.DTOs;

public sealed class ReviewResponse
{
    public Guid Id { get; init; }
    public Guid BookingId { get; init; }
    public Guid ListingId { get; init; }
    public Guid ReviewerId { get; init; }
    public Guid RevieweeId { get; init; }
    public ReviewerRole ReviewerRole { get; init; }
    public int Rating { get; init; }
    public string? Comment { get; init; }
    public DateTime CreatedAt { get; init; }
    public string ReviewerFirstName { get; init; } = string.Empty;
    public string ReviewerLastName { get; init; } = string.Empty;
    public string? ReviewerAvatarUrl { get; init; }
}
