using RentalPlatform.Application.Common;
using RentalPlatform.Application.DTOs;

namespace RentalPlatform.Application.Abstractions;

/// <summary>User-facing report submission (POST /api/reports). Any authenticated, non-blocked user.</summary>
public interface IReportsService
{
    Task<ServiceResult<ReportResponse>> CreateAsync(
        CreateReportRequest request, CancellationToken cancellationToken = default);
}
