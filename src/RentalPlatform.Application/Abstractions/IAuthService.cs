using RentalPlatform.Application.Common;
using RentalPlatform.Application.DTOs;

namespace RentalPlatform.Application.Abstractions;

public interface IAuthService
{
    Task<ServiceResult<AuthResponse>> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default);
    Task<ServiceResult<AuthResponse>> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default);
    Task<ServiceResult<AuthResponse>> ExternalAsync(ExternalAuthRequest request, CancellationToken cancellationToken = default);
    Task<ServiceResult<CurrentUserResponse>> GetCurrentUserAsync(CancellationToken cancellationToken = default);
    Task<ServiceResult<CurrentUserResponse>> UpdatePreferredLanguageAsync(string? preferredLanguage, CancellationToken cancellationToken = default);
    Task<ServiceResult<bool>> ChangePasswordAsync(string currentPassword, string newPassword, CancellationToken cancellationToken = default);

    // Home-point model: the home point is the only source of a user's listings' location.
    Task<ServiceResult<CurrentUserResponse>> UpdateHomePointAsync(decimal latitude, decimal longitude, CancellationToken cancellationToken = default);
    Task<ServiceResult<CurrentUserResponse>> ClearHomePointAsync(CancellationToken cancellationToken = default);
}
