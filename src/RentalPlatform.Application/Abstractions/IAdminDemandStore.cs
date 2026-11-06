using RentalPlatform.Application.DTOs;
using RentalPlatform.Domain.Entities;

namespace RentalPlatform.Application.Abstractions;

public interface IAdminDemandStore
{
    Task<User?> FindUserByIdAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Rows with Day in [from, to] inclusive, ordered by Day, DistrictId (NULL first), CategoryId.</summary>
    Task<IReadOnlyList<DemandRowResponse>> GetRowsAsync(
        DateOnly from, DateOnly to, CancellationToken cancellationToken = default);
}
