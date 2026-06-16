using System.ComponentModel.DataAnnotations;

namespace RentalPlatform.Application.DTOs;

public sealed class CreateToyReviewRequest
{
    [Required]
    public Guid BookingId { get; init; }

    [Range(1, 5)]
    public int OverallRating { get; init; }

    [Range(1, 5)]
    public int ConditionRating { get; init; }

    [Range(1, 5)]
    public int CleanlinessRating { get; init; }

    [Range(1, 5)]
    public int ValueForMoneyRating { get; init; }

    [Range(1, 5)]
    public int FunPlayValueRating { get; init; }

    [Range(1, 5)]
    public int DescriptionAccuracyRating { get; init; }

    [MaxLength(400)]
    public string? Comment { get; init; }
}
