using System.ComponentModel.DataAnnotations;

namespace RentalPlatform.Application.DTOs;

public sealed class CreateRenterReviewRequest
{
    [Required]
    public Guid BookingId { get; init; }

    [Range(1, 5)]
    public int CommunicationRating { get; init; }

    [Range(1, 5)]
    public int ReturnedOnTimeRating { get; init; }

    [Range(1, 5)]
    public int CareOfToyRating { get; init; }

    [Range(1, 5)]
    public int WouldRentAgainRating { get; init; }

    [MaxLength(400)]
    public string? Comment { get; init; }
}
