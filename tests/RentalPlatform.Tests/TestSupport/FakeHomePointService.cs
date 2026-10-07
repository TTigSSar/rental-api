using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.Common;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Infrastructure.Services;

namespace RentalPlatform.Tests.TestSupport;

// In-memory double for IHomePointService, for the AuthService tests that never touch a DbContext.
// It writes the same six User.Home* fields the real service writes, through the same
// HomePointDerivation helper and the same real GeohashSnapper/DistrictBoundaryProvider, so a
// registration test sees a genuinely snapped public pair and a genuinely derived district code.
//
// What it deliberately does NOT do is the listing fan-out or the notification fan-out — those need
// a database and belong to HomePointServiceTests, which runs the real service.
public sealed class FakeHomePointService : IHomePointService
{
    private readonly FakeUserAuthStore _store;
    private readonly IReadOnlyDictionary<string, Guid> _districtIdsByCode;
    private readonly GeohashSnapper _snapper = new();
    private readonly DistrictBoundaryProvider _boundaries = new();

    public FakeHomePointService(FakeUserAuthStore store, IReadOnlyDictionary<string, Guid>? districtIdsByCode = null)
    {
        _store = store;
        _districtIdsByCode = districtIdsByCode ?? new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
    }

    public List<(Guid UserId, decimal Latitude, decimal Longitude)> SetCalls { get; } = new();

    public List<Guid> ClearCalls { get; } = new();

    // The real Yerevan rule, through the real boundary provider — a fake that accepted anything
    // would let a registration test "pass" while the live endpoint rejected the same point.
    public ServiceResult<bool> ValidateForSave(decimal latitude, decimal longitude)
    {
        if (_boundaries.FindDistrictCode((double)latitude, (double)longitude) is null)
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
        SetCalls.Add((userId, latitude, longitude));

        var areaCheck = ValidateForSave(latitude, longitude);
        if (!areaCheck.IsSuccess)
        {
            return areaCheck;
        }

        var user = await FindAsync(userId, cancellationToken);
        if (user is null)
        {
            return ServiceResult<bool>.Failure(new ServiceError
            {
                Code = "auth.unauthenticated",
                Message = "Current user is not authenticated."
            });
        }

        var derived = HomePointDerivation.Derive(latitude, longitude, _snapper, _boundaries);

        user.HomeLatitude = Math.Round(latitude, 6, MidpointRounding.AwayFromZero);
        user.HomeLongitude = Math.Round(longitude, 6, MidpointRounding.AwayFromZero);
        user.HomePublicLatitude = derived.PublicLatitude;
        user.HomePublicLongitude = derived.PublicLongitude;
        user.HomeDistrictId = derived.DistrictCode is { } code && _districtIdsByCode.TryGetValue(code, out var districtId)
            ? districtId
            : null;
        user.HomePointUpdatedAt = DateTime.UtcNow;

        return ServiceResult<bool>.Success(true);
    }

    public async Task<ServiceResult<bool>> ClearHomePointAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        ClearCalls.Add(userId);

        var user = await FindAsync(userId, cancellationToken);
        if (user is null)
        {
            return ServiceResult<bool>.Failure(new ServiceError
            {
                Code = "auth.unauthenticated",
                Message = "Current user is not authenticated."
            });
        }

        user.HomeLatitude = null;
        user.HomeLongitude = null;
        user.HomePublicLatitude = null;
        user.HomePublicLongitude = null;
        user.HomeDistrictId = null;
        user.HomePointUpdatedAt = null;

        return ServiceResult<bool>.Success(true);
    }

    // The real store sees a user the moment AddAsync + SaveChangesAsync ran; the fake store only
    // exposes saved users, which is exactly the ordering AuthService.RegisterAsync relies on.
    private Task<User?> FindAsync(Guid userId, CancellationToken cancellationToken) =>
        _store.FindByIdAsync(userId, cancellationToken);
}
