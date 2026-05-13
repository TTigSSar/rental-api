using System.ComponentModel.DataAnnotations;

namespace RentalPlatform.Application.DTOs;

public sealed class CreateListingRequest
{
    [Required(ErrorMessage = "Category is required.")]
    public Guid CategoryId { get; init; }

    [Required(ErrorMessage = "Title is required.")]
    [StringLength(200, MinimumLength = 3, ErrorMessage = "Title must be between 3 and 200 characters.")]
    public string Title { get; init; } = string.Empty;

    [Required(ErrorMessage = "Description is required.")]
    [StringLength(4000, MinimumLength = 20, ErrorMessage = "Description must be between 20 and 4000 characters.")]
    public string Description { get; init; } = string.Empty;

    [Range(typeof(decimal), "0.01", "999999999999.99", ErrorMessage = "Price per day must be greater than zero.")]
    public decimal PricePerDay { get; init; }

    [Required(ErrorMessage = "Currency is required.")]
    [StringLength(3, MinimumLength = 3, ErrorMessage = "Currency must be a 3-letter ISO code (e.g. USD, AMD).")]
    public string Currency { get; init; } = string.Empty;

    [Required(ErrorMessage = "Country is required.")]
    [MaxLength(100)]
    public string Country { get; init; } = string.Empty;

    [Required(ErrorMessage = "City is required.")]
    [MaxLength(120)]
    public string City { get; init; } = string.Empty;

    [Required(ErrorMessage = "Address is required.")]
    [MaxLength(250)]
    public string AddressLine { get; init; } = string.Empty;

    [Range(typeof(decimal), "-90", "90", ErrorMessage = "Latitude must be between -90 and 90.")]
    public decimal Latitude { get; init; }

    [Range(typeof(decimal), "-180", "180", ErrorMessage = "Longitude must be between -180 and 180.")]
    public decimal Longitude { get; init; }

    // ---- Toy-rental MVP: optional toy-specific metadata (additive) ----
    // 600 months ≈ 50 years; bounded only to catch obvious data-entry errors.
    [Range(0, 600, ErrorMessage = "Age (from, months) must be between 0 and 600.")]
    public int? AgeFromMonths { get; init; }

    [Range(0, 600, ErrorMessage = "Age (to, months) must be between 0 and 600.")]
    public int? AgeToMonths { get; init; }

    [MaxLength(50, ErrorMessage = "Condition must be at most 50 characters.")]
    public string? Condition { get; init; }

    [MaxLength(1000, ErrorMessage = "Hygiene notes must be at most 1000 characters.")]
    public string? HygieneNotes { get; init; }

    [MaxLength(1000, ErrorMessage = "Safety notes must be at most 1000 characters.")]
    public string? SafetyNotes { get; init; }

    [Range(typeof(decimal), "0", "999999999999.99", ErrorMessage = "Deposit amount cannot be negative.")]
    public decimal? DepositAmount { get; init; }
}
