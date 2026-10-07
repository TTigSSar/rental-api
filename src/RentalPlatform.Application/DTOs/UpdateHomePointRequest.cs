using System.ComponentModel.DataAnnotations;
using RentalPlatform.Application.Common;

namespace RentalPlatform.Application.DTOs;

public sealed class UpdateHomePointRequest : IValidatableObject
{
    [Required(ErrorMessage = "Latitude is required.")]
    [Range(typeof(decimal), "-90", "90", ErrorMessage = "Latitude must be between -90 and 90.")]
    public decimal? Latitude { get; init; }

    [Required(ErrorMessage = "Longitude is required.")]
    [Range(typeof(decimal), "-180", "180", ErrorMessage = "Longitude must be between -180 and 180.")]
    public decimal? Longitude { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext) =>
        HomePointValidation.Validate(Latitude, Longitude, nameof(Latitude), nameof(Longitude));
}
