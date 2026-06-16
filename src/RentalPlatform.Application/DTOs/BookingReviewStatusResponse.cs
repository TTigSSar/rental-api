namespace RentalPlatform.Application.DTOs;

/// <summary>
/// What the current user can and has reviewed for a given booking. Drives the
/// review prompts and guards the review flow entry points.
/// </summary>
public sealed class BookingReviewStatusResponse
{
    public Guid BookingId { get; init; }

    /// <summary>"renter", "owner", or "none" — which side of the booking the caller is.</summary>
    public string Role { get; init; } = "none";

    public bool IsCompleted { get; init; }

    public bool CanReviewToy { get; init; }
    public bool CanReviewOwner { get; init; }
    public bool CanReviewRenter { get; init; }

    public bool HasToyReview { get; init; }
    public bool HasOwnerReview { get; init; }
    public bool HasRenterReview { get; init; }
}
