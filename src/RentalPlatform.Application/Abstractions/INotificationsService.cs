using RentalPlatform.Application.Common;
using RentalPlatform.Application.DTOs;

namespace RentalPlatform.Application.Abstractions;

public interface INotificationsService
{
    Task<ServiceResult<NotificationFeedResponse>> GetFeedAsync(
        string? filter,
        string? cursor,
        CancellationToken cancellationToken = default);

    Task<ServiceResult<NotificationUnreadCountResponse>> GetUnreadCountAsync(
        CancellationToken cancellationToken = default);

    Task<ServiceResult<bool>> MarkReadAsync(Guid notificationId, CancellationToken cancellationToken = default);

    Task<ServiceResult<bool>> MarkAllReadAsync(CancellationToken cancellationToken = default);
}
