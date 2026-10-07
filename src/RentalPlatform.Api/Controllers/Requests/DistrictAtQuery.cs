using System.ComponentModel.DataAnnotations;

namespace RentalPlatform.Api.Controllers.Requests;

/// <summary>
/// Query string of GET /api/districts/at.
/// </summary>
/// <remarks>
/// Only the WGS84 range is enforced here. Whether the point is in Yerevan deliberately is NOT:
/// reporting that is this endpoint's entire job, so a point outside the service area must answer
/// 200 with a null district, never 400. The refusal belongs on the write side
/// (IHomePointService.ValidateForSave → auth.home_point_outside_yerevan).
/// </remarks>
public sealed class DistrictAtQuery
{
    [Required(ErrorMessage = "Latitude is required.")]
    [Range(typeof(decimal), "-90", "90", ErrorMessage = "Latitude must be between -90 and 90.")]
    public decimal? Lat { get; init; }

    [Required(ErrorMessage = "Longitude is required.")]
    [Range(typeof(decimal), "-180", "180", ErrorMessage = "Longitude must be between -180 and 180.")]
    public decimal? Lng { get; init; }
}
