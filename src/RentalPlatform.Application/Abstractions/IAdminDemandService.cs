using RentalPlatform.Application.Common;
using RentalPlatform.Application.DTOs;

namespace RentalPlatform.Application.Abstractions;

public interface IAdminDemandService
{
    /// <summary>
    /// <paramref name="from"/> and <paramref name="to"/> are the raw query strings (yyyy-MM-dd); both or
    /// neither. Neither means today (Yerevan) and the 29 days before it.
    /// </summary>
    Task<ServiceResult<DemandAnalyticsResponse>> GetDemandAsync(
        string? from, string? to, CancellationToken cancellationToken = default);
}
