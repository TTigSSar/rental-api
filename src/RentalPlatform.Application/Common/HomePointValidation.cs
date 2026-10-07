using System.ComponentModel.DataAnnotations;

namespace RentalPlatform.Application.Common;

// Shared IValidatableObject body for every DTO that accepts a home point — RegisterRequest (where the
// point is optional, so both-or-neither is a real rule) and UpdateHomePointRequest (where both are
// [Required] anyway, so this is a no-op safety net).
//
// WHERE THE POINT IS is deliberately NOT checked here. DoRent operates in Yerevan only, and "is this
// coordinate in Yerevan?" is answered by a point-in-polygon lookup against the district boundary
// asset (IDistrictBoundaryProvider) — a service, which a DataAnnotations attribute has no business
// reaching. That check lives in IHomePointService instead and fails with
// HomePointErrorCodes.OutsideYerevan. Keeping it in exactly one place is the point: a second,
// cheaper approximation here (the coarse Armenia bounding box this class used to hold) could accept
// a point the real check then rejects, which is precisely the disagreement that makes a map say
// "fine" about a pin the save refuses.
public static class HomePointValidation
{
    public static IEnumerable<ValidationResult> Validate(
        decimal? latitude,
        decimal? longitude,
        string latitudeMemberName = "Latitude",
        string longitudeMemberName = "Longitude")
    {
        if (latitude is null && longitude is null)
        {
            yield break;
        }

        if (latitude is null || longitude is null)
        {
            yield return new ValidationResult(
                "Both latitude and longitude are required together.",
                new[] { latitudeMemberName, longitudeMemberName });
        }
    }
}
