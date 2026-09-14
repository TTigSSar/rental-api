using RentalPlatform.Application.Common;
using RentalPlatform.Application.DTOs;

namespace RentalPlatform.Application.Abstractions;

public interface IAdminMessagesService
{
    Task<ServiceResult<AdminMessageThreadQueueResponse>> GetThreadsAsync(
        AdminMessageThreadFilter filter,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get-or-create the single Moderation thread for <paramref name="userId"/>, opened (or
    /// continued) by the calling admin. Rejects opening a thread with another Admin or with the
    /// caller's own account.
    /// </summary>
    Task<ServiceResult<AdminMessageThreadResponse>> OpenThreadAsync(
        Guid userId,
        CancellationToken cancellationToken = default);
}
