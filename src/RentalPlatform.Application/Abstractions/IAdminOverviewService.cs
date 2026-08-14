using RentalPlatform.Application.Common;
using RentalPlatform.Application.DTOs;

namespace RentalPlatform.Application.Abstractions;

public interface IAdminOverviewService
{
    Task<ServiceResult<AdminOverviewResponse>> GetOverviewAsync(CancellationToken cancellationToken = default);

    Task<ServiceResult<AdminActivityFeedResponse>> GetActivityFeedAsync(
        int take, CancellationToken cancellationToken = default);
}
