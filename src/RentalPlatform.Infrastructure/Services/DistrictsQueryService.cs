using Microsoft.EntityFrameworkCore;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.DTOs;
using RentalPlatform.Infrastructure.Persistence;

namespace RentalPlatform.Infrastructure.Services;

public sealed class DistrictsQueryService : IDistrictsQueryService
{
    private readonly AppDbContext _dbContext;

    public DistrictsQueryService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
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
}
