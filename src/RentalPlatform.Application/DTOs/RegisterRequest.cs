using System.ComponentModel.DataAnnotations;
using RentalPlatform.Application.Common;

namespace RentalPlatform.Application.DTOs;

public sealed class RegisterRequest : IValidatableObject
{
    [Required]
    [EmailAddress]
    [MaxLength(320)]
    public string Email { get; set; } = string.Empty;

    [Required]
    [MinLength(8)]
    [MaxLength(128)]
    public string Password { get; set; } = string.Empty;

    [Required]
    [MaxLength(100)]
    public string FirstName { get; set; } = string.Empty;

    [Required]
    [MaxLength(100)]
    public string LastName { get; set; } = string.Empty;

    [Required]
    [RegularExpression(@"^\+?(?=(?:[^\d]*\d){7,20}[^\d]*$)[\d\s\-().]+$", ErrorMessage = "Enter a valid phone number.")]
    [MaxLength(32)]
    public string PhoneNumber { get; set; } = string.Empty;

    [MaxLength(16)]
    public string? PreferredLanguage { get; set; }

    // Optional home-point step (skippable at sign-up — the home-point model). Both-or-neither: see Validate.
    [Range(typeof(decimal), "-90", "90", ErrorMessage = "Latitude must be between -90 and 90.")]
    public decimal? HomeLatitude { get; set; }

    [Range(typeof(decimal), "-180", "180", ErrorMessage = "Longitude must be between -180 and 180.")]
    public decimal? HomeLongitude { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext) =>
        HomePointValidation.Validate(HomeLatitude, HomeLongitude, nameof(HomeLatitude), nameof(HomeLongitude));
}
