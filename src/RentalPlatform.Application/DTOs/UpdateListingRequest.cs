using System.ComponentModel.DataAnnotations;
using RentalPlatform.Application.Common;
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

    // Clearable optionals (ADR-040) - three states, carried by Optional<T>:
    //   property ABSENT  -> leave unchanged   (IsSet == false)
    //   explicit `null`  -> clear to null     (IsSet == true, Value == null)
    //   a value          -> set it
    // Blank/whitespace text still normalises to null (clears). Range/length checks live in Validate()
    // because validation attributes cannot see through the wrapper; messages are unchanged.
    public Optional<int?> AgeFromMonths { get; init; }

    public Optional<int?> AgeToMonths { get; init; }

    public Optional<string?> Condition { get; init; }

    public Optional<string?> HygieneNotes { get; init; }

    public Optional<string?> SafetyNotes { get; init; }

    // Absent leaves the amount unchanged; a value sets it. Unlike the clearable optionals an explicit
    // `null` is rejected (ADR-018: the amount is required once set) - see Validate(). See
    // CreateListingRequest.CompensationAmount for the "Loss & damage compensation" semantics.
    public Optional<decimal?> CompensationAmount { get; init; }

    // Clearable optional (ADR-040): shortest number of days a renter may book for. Absent leaves it
    // unchanged, explicit `null` clears it, a value sets it.
    public Optional<int?> MinRentalDays { get; init; }

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
        if (AgeFromMonths is { IsSet: true, Value: { } ageFrom } && (ageFrom < 0 || ageFrom > 600))
        {
            yield return new ValidationResult(
                "Age (from, months) must be between 0 and 600.",
                new[] { nameof(AgeFromMonths) });
        }

        if (AgeToMonths is { IsSet: true, Value: { } ageTo } && (ageTo < 0 || ageTo > 600))
        {
            yield return new ValidationResult(
                "Age (to, months) must be between 0 and 600.",
                new[] { nameof(AgeToMonths) });
        }

        if (Condition is { IsSet: true, Value: { } condition } && condition.Length > 50)
        {
            yield return new ValidationResult(
                "Condition must be at most 50 characters.",
                new[] { nameof(Condition) });
        }

        if (HygieneNotes is { IsSet: true, Value: { } hygieneNotes } && hygieneNotes.Length > 1000)
        {
            yield return new ValidationResult(
                "Hygiene notes must be at most 1000 characters.",
                new[] { nameof(HygieneNotes) });
        }

        if (SafetyNotes is { IsSet: true, Value: { } safetyNotes } && safetyNotes.Length > 1000)
        {
            yield return new ValidationResult(
                "Safety notes must be at most 1000 characters.",
                new[] { nameof(SafetyNotes) });
        }

        if (CompensationAmount.IsSet)
        {
            if (CompensationAmount.Value is not { } compensation)
            {
                yield return new ValidationResult(
                    "Loss & damage compensation cannot be cleared.",
                    new[] { nameof(CompensationAmount) });
            }
            else if (compensation < 1000m || compensation > 10000000m)
            {
                yield return new ValidationResult(
                    "Loss & damage compensation must be between 1,000 and 10,000,000 AMD.",
                    new[] { nameof(CompensationAmount) });
            }
        }

        if (MinRentalDays is { IsSet: true, Value: { } minDays } && (minDays < 1 || minDays > 365))
        {
            yield return new ValidationResult(
                "Minimum rental days must be between 1 and 365.",
                new[] { nameof(MinRentalDays) });
        }

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
