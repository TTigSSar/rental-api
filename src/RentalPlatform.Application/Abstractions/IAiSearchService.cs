using RentalPlatform.Application.Common;
using RentalPlatform.Application.DTOs;

namespace RentalPlatform.Application.Abstractions;

public interface IAiSearchService
{
    /// <param name="language">UI language for the intent line: en, ru or hy.</param>
    Task<ServiceResult<AiSearchResponse>> SearchAsync(
        AiSearchRequest request,
        string language,
        CancellationToken cancellationToken = default);

    /// <summary>False when no API key is configured or today's spend has reached the daily budget.</summary>
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);
}
