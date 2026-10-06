using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.Common;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Application.Services;

// The single writer for User.Home*/Listing location fields (home-point model). See IHomePointService for
// the contract. Callers: AuthService (register, PUT/DELETE me/home-point), the development seed
// and the demo bootstrap.
public sealed class HomePointService : IHomePointService
{
    private static class ErrorCodes
    {
        public const string Unauthenticated = "auth.unauthenticated";
        public const string UserBlocked = "auth.user_blocked";
    }

    private readonly IHomePointStore _store;
    private readonly IGeohashSnapper _geohashSnapper;
    private readonly IDistrictBoundaryProvider _districtBoundaryProvider;
    private readonly INotificationEmitter _notificationEmitter;

    public HomePointService(
        IHomePointStore store,
        IGeohashSnapper geohashSnapper,
        IDistrictBoundaryProvider districtBoundaryProvider,
        INotificationEmitter notificationEmitter)
    {
        _store = store;
        _geohashSnapper = geohashSnapper;
        _districtBoundaryProvider = districtBoundaryProvider;
        _notificationEmitter = notificationEmitter;
    }

    // DoRent operates in Yerevan only, so "inside a district" IS the validity rule — there is no
    // separate country-level check, and no bounding box. One lookup, one answer.
    public ServiceResult<bool> ValidateForSave(decimal latitude, decimal longitude)
    {
        var districtCode = _districtBoundaryProvider.FindDistrictCode((double)latitude, (double)longitude);
        if (districtCode is null)
        {
            return ServiceResult<bool>.Failure(new ServiceError
            {
                Code = HomePointErrorCodes.OutsideYerevan,
                Message = "For now DoRent works only in Yerevan — move the pin inside the city."
            });
        }

        return ServiceResult<bool>.Success(true);
    }

    public async Task<ServiceResult<bool>> SetHomePointAsync(
        Guid userId,
        decimal latitude,
        decimal longitude,
        CancellationToken cancellationToken = default)
    {
        // Checked before anything is read or written: this is the single writer, so the Yerevan rule
        // has to hold here regardless of whether the caller already checked. The dev seed and the
        // demo bootstrap come through this path too, which is why both only ever declare Yerevan
        // points.
        var areaCheck = ValidateForSave(latitude, longitude);
        if (!areaCheck.IsSuccess)
        {
            return areaCheck;
        }

        var user = await _store.FindUserByIdAsync(userId, cancellationToken);
        if (user is null)
        {
            return ServiceResult<bool>.Failure(new ServiceError
            {
                Code = ErrorCodes.Unauthenticated,
                Message = "Current user is not authenticated."
            });
        }

        if (user.IsBlocked)
        {
            return ServiceResult<bool>.Failure(new ServiceError
            {
                Code = ErrorCodes.UserBlocked,
                Message = "Blocked users cannot set a home point."
            });
        }

        // Fixed-scale round to match the decimal(9,6) column — keeps the "unchanged" comparison and
        // the persisted value in agreement regardless of how many digits the caller supplied.
        var roundedLatitude = Math.Round(latitude, 6, MidpointRounding.AwayFromZero);
        var roundedLongitude = Math.Round(longitude, 6, MidpointRounding.AwayFromZero);

        if (user.HomeLatitude == roundedLatitude && user.HomeLongitude == roundedLongitude)
        {
            return ServiceResult<bool>.Success(false);
        }

        var derived = HomePointDerivation.Derive(roundedLatitude, roundedLongitude, _geohashSnapper, _districtBoundaryProvider);
        var districtId = derived.DistrictCode is null
            ? (Guid?)null
            : await _store.FindDistrictIdByCodeAsync(derived.DistrictCode, cancellationToken);

        var publicPairChanged = user.HomePublicLatitude != derived.PublicLatitude || user.HomePublicLongitude != derived.PublicLongitude;
        var districtChanged = user.HomeDistrictId != districtId;

        user.HomeLatitude = roundedLatitude;
        user.HomeLongitude = roundedLongitude;
        user.HomePublicLatitude = derived.PublicLatitude;
        user.HomePublicLongitude = derived.PublicLongitude;
        user.HomeDistrictId = districtId;
        user.HomePointUpdatedAt = DateTime.UtcNow;

        var listings = await _store.GetListingsByOwnerIdAsync(userId, cancellationToken);
        foreach (var listing in listings)
        {
            listing.Latitude = roundedLatitude;
            listing.Longitude = roundedLongitude;
            listing.PublicLatitude = derived.PublicLatitude;
            listing.PublicLongitude = derived.PublicLongitude;
            listing.DistrictId = districtId;
            listing.LocationKind = LocationKind.Home;

            // City moves with the pin. It is the one location field nothing can derive from a
            // coordinate (there is deliberately no geocoder in the correctness path), so it is the
            // one that could silently end up contradicting the map — an owner who moved would keep
            // advertising the city they left. Resolving to a district IS the statement "this is
            // Yerevan", so that is when it is rewritten.
            //
            // When no district resolved, the existing value is left alone rather than blanked or
            // guessed. That case is now reachable only for a legacy home point the migration derived
            // from a listing outside Yerevan (new writes are refused above), and for those rows the
            // city they already carry is the best information anyone has.
            if (districtId is not null)
            {
                listing.City = LocationDefaults.YerevanCity;
            }

            listing.Country = LocationDefaults.Country;

            // Deliberately untouched: Status, UpdatedAt. A home move is never re-moderation (home-point model).
        }

        await _store.SaveChangesAsync(cancellationToken);

        // Strictly after the commit, and strictly best-effort. The renter-facing notification is a
        // courtesy; the move is the operation. Nothing below this line may fail the call — same
        // contract as IEmailService ("never throws, never rolls back the main operation").
        //
        // This outer catch is the LAST resort, not the only one. It used to be the only one, with a
        // comment claiming the emitter logged everything itself — which was false: the emitter's own
        // try/catch started inside EmitAsync, after it had already resolved a language, rendered the
        // copy and read booking.Listing.Title, so one booking with an unloaded or deleted Listing
        // threw out here, aborted the fan-out, and silently cost every REMAINING renter their
        // notification. The emitter now guards and logs its whole body (NotificationEmitter.
        // PickupAreaChangedAsync), and the fan-out below isolates each booking so one bad row costs
        // one notification instead of all of them. By the time anything reaches this catch the
        // emitter has already logged it; the Application layer has no logging abstraction by design
        // (it references Domain only), which is exactly why the log line lives at the emitter.
        if (publicPairChanged || districtChanged)
        {
            try
            {
                await EmitPickupNotificationsAsync(user, districtId, cancellationToken);
            }
            catch
            {
                // Swallowed on purpose — see above.
            }
        }

        return ServiceResult<bool>.Success(true);
    }

    public async Task<ServiceResult<bool>> ClearHomePointAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await _store.FindUserByIdAsync(userId, cancellationToken);
        if (user is null)
        {
            return ServiceResult<bool>.Failure(new ServiceError
            {
                Code = ErrorCodes.Unauthenticated,
                Message = "Current user is not authenticated."
            });
        }

        if (user.IsBlocked)
        {
            return ServiceResult<bool>.Failure(new ServiceError
            {
                Code = ErrorCodes.UserBlocked,
                Message = "Blocked users cannot change a home point."
            });
        }

        if (await _store.OwnerHasAnyListingAsync(userId, cancellationToken))
        {
            return ServiceResult<bool>.Failure(new ServiceError
            {
                Code = HomePointErrorCodes.InUse,
                Message = "Remove or transfer your listings before clearing your home point."
            });
        }

        if (user.HomeLatitude is null && user.HomeLongitude is null)
        {
            return ServiceResult<bool>.Success(false);
        }

        // All six fields go back to null — HomePointUpdatedAt included. It is the "when was this
        // point last placed" stamp shown next to the point itself, so keeping a timestamp for a
        // point that no longer exists would render as a home point the user has just deleted.
        user.HomeLatitude = null;
        user.HomeLongitude = null;
        user.HomePublicLatitude = null;
        user.HomePublicLongitude = null;
        user.HomeDistrictId = null;
        user.HomePointUpdatedAt = null;

        await _store.SaveChangesAsync(cancellationToken);

        return ServiceResult<bool>.Success(true);
    }

    // Best-effort: renters with a Pending/Approved/Active booking against one of this owner's
    // listings get notified their pickup area moved. A bad row here can never roll back the
    // home-point change that already committed above.
    private async Task EmitPickupNotificationsAsync(User owner, Guid? districtId, CancellationToken cancellationToken)
    {
        var district = districtId is null
            ? null
            : await _store.FindDistrictByIdAsync(districtId.Value, cancellationToken);

        var bookings = await _store.GetInFlightBookingsByOwnerAsync(owner.Id, cancellationToken);
        foreach (var booking in bookings)
        {
            // Per booking, not per fan-out. This loop is the blast radius: one renter's
            // notification failing must cost that one renter, not everybody further down the list —
            // and "further down the list" is unbounded, since a busy owner can have dozens of
            // bookings in flight. The emitter logs whatever it failed on (it owns the only logger at
            // this seam), so this catch is deliberately silent and deliberately per-iteration.
            try
            {
                await _notificationEmitter.PickupAreaChangedAsync(booking, owner, district, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Next booking. Cancellation is excluded on purpose: if the request is going away
                // there is no "next booking" worth attempting, and it belongs to the caller.
            }
        }
    }
}
