using Microsoft.EntityFrameworkCore;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.Common;
using RentalPlatform.Application.DTOs;
using RentalPlatform.Infrastructure.Persistence;

namespace RentalPlatform.Infrastructure.Services;

public sealed class DistrictsQueryService : IDistrictsQueryService
{
    private readonly AppDbContext _dbContext;
    private readonly IDistrictBoundaryProvider _districtBoundaryProvider;

    public DistrictsQueryService(AppDbContext dbContext, IDistrictBoundaryProvider districtBoundaryProvider)
    {
        _dbContext = dbContext;
        _districtBoundaryProvider = districtBoundaryProvider;
    }

    public async Task<IReadOnlyCollection<ListingDistrictResponse>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await _dbContext.Districts
            .AsNoTracking()
            .OrderBy(district => district.NameEn)
            .Select(district => new ListingDistrictResponse
            {
                Id = district.Id,
                Code = district.Code,
                NameEn = district.NameEn,
                NameHy = district.NameHy,
                NameRu = district.NameRu
            })
            .ToListAsync(cancellationToken);
    }

    public async Task<DistrictAtResponse> FindAtAsync(
        decimal latitude,
        decimal longitude,
        CancellationToken cancellationToken = default)
    {
        // The same lookup, against the same boundary asset, that IHomePointService.ValidateForSave
        // uses to accept or refuse a save. That is deliberate and it is the whole contract of this
        // endpoint: a non-null district here means the point is saveable, so the map can never show
        // a green district name for a pin the save would then reject.
        var code = _districtBoundaryProvider.FindDistrictCode((double)latitude, (double)longitude);
        if (code is null)
        {
            return new DistrictAtResponse { District = null };
        }

        var district = await _dbContext.Districts
            .AsNoTracking()
            .Where(district => district.Code == code)
            .Select(district => new ListingDistrictResponse
            {
                Id = district.Id,
                Code = district.Code,
                NameEn = district.NameEn,
                NameHy = district.NameHy,
                NameRu = district.NameRu
            })
            .FirstOrDefaultAsync(cancellationToken);

        return new DistrictAtResponse { District = district };
    }
}
