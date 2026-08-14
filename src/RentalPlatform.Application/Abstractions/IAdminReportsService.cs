using RentalPlatform.Application.Common;
using RentalPlatform.Application.DTOs;

namespace RentalPlatform.Application.Abstractions;

/// <summary>Admin console Phase 4: the Reports & flags screen. Admin-only (re-checked in the
/// service, defence in depth alongside the controller's [Authorize(Roles = "Admin")]).</summary>
public interface IAdminReportsService
{
    Task<ServiceResult<AdminReportQueueResponse>> GetQueueAsync(
        AdminReportQueueFilter filter, CancellationToken cancellationToken = default);

    Task<ServiceResult<AdminReportRowResponse>> GetByIdAsync(
        Guid reportId, CancellationToken cancellationToken = default);

    Task<ServiceResult<AdminReportRowResponse>> ResolveAsync(
        Guid reportId, string? note, CancellationToken cancellationToken = default);

    Task<ServiceResult<AdminReportRowResponse>> DismissAsync(
        Guid reportId, string? note, CancellationToken cancellationToken = default);

    Task<ServiceResult<AdminReportRowResponse>> ReopenAsync(
        Guid reportId, CancellationToken cancellationToken = default);
}
