using System.ComponentModel.DataAnnotations;

namespace RentalPlatform.Application.DTOs;

public sealed class CreateListingRequest
{
    [Required]
    public Guid CategoryId { get; init; }

    [Required]
    [MaxLength(200)]
    public string Title { get; init; } = string.Empty;

    [Required]
    [MaxLength(4000)]
    public string Description { get; init; } = string.Empty;

    [Range(typeof(decimal), "0.01", "999999999999.99")]
    public decimal PricePerDay { get; init; }

    [Required]
    [StringLength(3, MinimumLength = 3)]
    public string Currency { get; init; } = string.Empty;

    [Required]
    [MaxLength(100)]
    public string Country { get; init; } = string.Empty;

    [Required]
    [MaxLength(120)]
    public string City { get; init; } = string.Empty;

    [Required]
    [MaxLength(250)]
    public string AddressLine { get; init; } = string.Empty;

    [Range(typeof(decimal), "-90", "90")]
    public decimal Latitude { get; init; }

    [Range(typeof(decimal), "-180", "180")]
    public decimal Longitude { get; init; }
}
