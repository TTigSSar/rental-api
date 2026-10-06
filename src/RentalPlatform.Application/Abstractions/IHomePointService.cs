using RentalPlatform.Application.Common;

namespace RentalPlatform.Application.Abstractions;

// The single writer for a user's home point and the denormalised copy on every listing they own
// (home-point model). Used by AuthService (register / PUT me/home-point / DELETE me/home-point), the
// development seed and the demo bootstrap — nobody else ever assigns User.Home*/Listing location
// fields directly.
public interface IHomePointService
{
    /// <summary>
    /// Whether a coordinate may be saved as a home point: it must fall inside one of the 12 Yerevan
    /// districts. Pure (no database), so a caller can check BEFORE it has a user row to write to.
    /// </summary>
    /// <remarks>
    /// Exists for registration, which must reject an out-of-area point WITHOUT having created an
    /// account first — otherwise a bad pin on the sign-up map would leave a half-registered user
    /// behind and the retry would then fail on a duplicate email. Every other caller can just let
    /// <see cref="SetHomePointAsync"/> perform the same check itself.
    /// </remarks>
    ServiceResult<bool> ValidateForSave(decimal latitude, decimal longitude);

    // WGS84 range is assumed already validated by the caller (the DTOs' [Range] attributes) — same
    // convention as IGeohashSnapper.SnapToCellCenter. WHERE the point is, however, is validated
    // here: a point outside every Yerevan district fails with HomePointErrorCodes.OutsideYerevan,
    // because this is the single writer and the rule must hold no matter who calls it.
    //
    // Unknown/blocked user -> failure. Setting the same point the user already has is a no-op
    // (returns Success(false), nothing written).
    Task<ServiceResult<bool>> SetHomePointAsync(
        Guid userId,
        decimal latitude,
        decimal longitude,
        CancellationToken cancellationToken = default);

    // Fails with "auth.home_point_in_use" when the user owns any listing (any status) — the renter
    // privacy exit is only available to users who never became an owner.
    Task<ServiceResult<bool>> ClearHomePointAsync(
        Guid userId,
        CancellationToken cancellationToken = default);
}
