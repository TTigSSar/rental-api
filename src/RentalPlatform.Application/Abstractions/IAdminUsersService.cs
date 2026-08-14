using RentalPlatform.Application.Common;
using RentalPlatform.Application.DTOs;

namespace RentalPlatform.Application.Abstractions;

public interface IAdminUsersService
{
    Task<ServiceResult<AdminUserQueueResponse>> GetQueueAsync(
        AdminUserQueueFilter filter,
        CancellationToken cancellationToken = default);

    Task<ServiceResult<AdminUserSummaryResponse>> GetByIdAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    Task<ServiceResult<AdminUserSummaryResponse>> VerifyAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    Task<ServiceResult<AdminUserSummaryResponse>> SuspendAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    Task<ServiceResult<AdminUserSummaryResponse>> ReactivateAsync(
        Guid userId,
        CancellationToken cancellationToken = default);
}
