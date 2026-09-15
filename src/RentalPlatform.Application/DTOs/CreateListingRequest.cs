using System.ComponentModel.DataAnnotations;
using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Application.DTOs;

public sealed class CreateListingRequest : IValidatableObject
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

    // The rental period the price applies to (Hourly/Daily/Weekly/Monthly/Yearly).
    // Optional: defaults to Daily when omitted. Validated to be a defined enum value.
    [EnumDataType(typeof(PriceUnit), ErrorMessage = "Price unit must be one of Hourly, Daily, Weekly, Monthly, Yearly.")]
    public PriceUnit? PriceUnit { get; init; }

    // Optional: 3-letter ISO code. Defaults to AMD when omitted.
    [StringLength(3, MinimumLength = 3, ErrorMessage = "Currency must be a 3-letter ISO code (e.g. USD, AMD).")]
    public string? Currency { get; init; }

    [Required(ErrorMessage = "Country is required.")]
    [MaxLength(100, ErrorMessage = "Country must be at most 100 characters.")]
    public string Country { get; init; } = string.Empty;

    [Required(ErrorMessage = "City is required.")]
    [MaxLength(120, ErrorMessage = "City must be at most 120 characters.")]
    public string City { get; init; } = string.Empty;

    [MaxLength(250, ErrorMessage = "Address must be at most 250 characters.")]
    public string? AddressLine { get; init; }

    [Range(typeof(decimal), "-90", "90", ErrorMessage = "Latitude must be between -90 and 90.")]
    public decimal? Latitude { get; init; }

    [Range(typeof(decimal), "-180", "180", ErrorMessage = "Longitude must be between -180 and 180.")]
    public decimal? Longitude { get; init; }

    // Optional owner override for the derived district (point-in-polygon against Latitude/
    // Longitude — see IDistrictBoundaryProvider). When supplied it must reference an existing
    // District row and wins over derivation; when omitted, the district is derived from the exact
    // point (and may be null if that point falls outside every known Yerevan district).
    public Guid? DistrictId { get; init; }

    // ---- Toy-rental MVP: optional toy-specific metadata ----
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

    // Optional: shortest number of days a renter may book for. Omitted when the owner doesn't set one.
    [Range(1, 365, ErrorMessage = "Minimum rental days must be between 1 and 365.")]
    public int? MinRentalDays { get; init; }

    // Optional: how the toy is handed over (Pickup/Courier). Validated to be a defined enum value.
    // Legacy single-select field — kept for backward compatibility. See DeliveryTypes below.
    [EnumDataType(typeof(DeliveryType), ErrorMessage = "Delivery type must be one of Pickup, Courier.")]
    public DeliveryType? DeliveryType { get; init; }

    // Additive multi-select successor to DeliveryType: the wizard may now offer Pickup, Courier,
    // or both. Optional; when supplied it must be non-empty and every value must be a defined
    // DeliveryType (see Validate below) — duplicates are tolerated and collapsed by the service.
    // When both this and DeliveryType are supplied, this list wins (see DeliveryOptionsMapper).
    public IReadOnlyList<DeliveryType>? DeliveryTypes { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (DeliveryTypes is null)
        {
            yield break;
        }

        if (DeliveryTypes.Count == 0)
        {
            yield return new ValidationResult(
                "Delivery types must not be empty when supplied.",
                new[] { nameof(DeliveryTypes) });
        }
        else if (DeliveryTypes.Any(value => !Enum.IsDefined(typeof(DeliveryType), value)))
        {
            yield return new ValidationResult(
                "Delivery types must each be one of Pickup, Courier.",
                new[] { nameof(DeliveryTypes) });
        }
    }
}
