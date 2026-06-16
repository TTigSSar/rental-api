using System.ComponentModel.DataAnnotations;

namespace RentalPlatform.Application.DTOs;

public sealed class CreateOwnerReviewRequest
{
    [Required]
    public Guid BookingId { get; init; }

    [Range(1, 5)]
    public int CommunicationRating { get; init; }

    [Range(1, 5)]
    public int PickupHandoverRating { get; init; }

    [Range(1, 5)]
    public int FriendlinessRating { get; init; }

    [MaxLength(400)]
    public string? Comment { get; init; }
}
