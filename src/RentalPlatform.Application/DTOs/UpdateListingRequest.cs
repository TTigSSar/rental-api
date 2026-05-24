using System.ComponentModel.DataAnnotations;

namespace RentalPlatform.Application.DTOs;

public sealed class UpdateListingRequest
{
    [StringLength(200, MinimumLength = 3, ErrorMessage = "Title must be between 3 and 200 characters.")]
    public string? Title { get; init; }

    [StringLength(4000, MinimumLength = 20, ErrorMessage = "Description must be between 20 and 4000 characters.")]
    public string? Description { get; init; }

    [Range(typeof(decimal), "0.01", "999999999999.99", ErrorMessage = "Price per day must be greater than zero.")]
    public decimal? PricePerDay { get; init; }

    [MaxLength(120, ErrorMessage = "City must be at most 120 characters.")]
    public string? City { get; init; }

    [MaxLength(100, ErrorMessage = "Country must be at most 100 characters.")]
    public string? Country { get; init; }

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
