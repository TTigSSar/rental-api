using System.ComponentModel.DataAnnotations;
using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Application.DTOs;

public sealed class UpdateListingRequest : IValidatableObject
{
    [StringLength(200, MinimumLength = 3, ErrorMessage = "Title must be between 3 and 200 characters.")]
    public string? Title { get; init; }

    [StringLength(4000, MinimumLength = 20, ErrorMessage = "Description must be between 20 and 4000 characters.")]
    public string? Description { get; init; }

    [Range(typeof(decimal), "0.01", "999999999999.99", ErrorMessage = "Price per day must be greater than zero.")]
    public decimal? PricePerDay { get; init; }

    // The rental period the price applies to. Null leaves the existing unit unchanged;
    // when supplied it must be a defined enum value.
    [EnumDataType(typeof(PriceUnit), ErrorMessage = "Price unit must be one of Hourly, Daily, Weekly, Monthly, Yearly.")]
    public PriceUnit? PriceUnit { get; init; }

    // Country, city and district overrides were removed (home-point model) — a listing's country,
    // city and district are always derived from the owner's home point, and only HomePointService
    // (or create) ever writes them. A stale client that still sends `country`/`city`/`districtId`
    // binds harmlessly: unknown JSON members are ignored, so nothing is applied.
    //
    // Country went last, and it was the inconsistency rather than the rule: it stayed writable
    // behind nothing but a length check while HomePointService re-asserts it on every move and
    // create hardcodes it, so `{"country":"Neverland"}` stuck forever — no re-moderation, and
    // neither City nor Country is reconciled by ListingLocationBackfillRunner — on a listing
    // publishing a Yerevan district and a Yerevan pin beside it.

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

    // Optional (partial update): null leaves the existing amount unchanged. See
    // CreateListingRequest.CompensationAmount for the "Loss & damage compensation" semantics.
    [Range(typeof(decimal), "1000", "10000000", ErrorMessage = "Loss & damage compensation must be between 1,000 and 10,000,000 AMD.")]
    public decimal? CompensationAmount { get; init; }

    // Optional: shortest number of days a renter may book for. Null leaves the existing value unchanged.
    [Range(1, 365, ErrorMessage = "Minimum rental days must be between 1 and 365.")]
    public int? MinRentalDays { get; init; }

    // Optional: how the toy is handed over. Null leaves the existing value unchanged; when supplied
    // it must be a defined enum value. Legacy single-select field — see DeliveryTypes below.
    [EnumDataType(typeof(DeliveryType), ErrorMessage = "Delivery type must be one of Pickup, Courier.")]
    public DeliveryType? DeliveryType { get; init; }

    // Additive multi-select successor to DeliveryType. Null leaves the listing's delivery options
    // unchanged (this is a structured field and never triggers re-moderation). When supplied it
    // must be non-empty and every value must be a defined DeliveryType (see Validate below) —
    // duplicates are tolerated and collapsed by the service. Wins over DeliveryType when both are
    // supplied (see DeliveryOptionsMapper).
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
