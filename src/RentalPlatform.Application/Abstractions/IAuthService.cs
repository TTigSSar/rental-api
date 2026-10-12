using RentalPlatform.Application.Common;
using RentalPlatform.Application.DTOs;

namespace RentalPlatform.Application.Abstractions;

public interface IAuthService
{
    Task<ServiceResult<RegisterResponse>> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default);
    Task<ServiceResult<AuthResponse>> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default);
    Task<ServiceResult<AuthResponse>> VerifyEmailAsync(VerifyEmailRequest request, CancellationToken cancellationToken = default);
    Task<ServiceResult<bool>> ResendVerificationAsync(ResendVerificationRequest request, CancellationToken cancellationToken = default);
    Task<ServiceResult<AuthResponse>> ExternalAsync(ExternalAuthRequest request, CancellationToken cancellationToken = default);
    Task<ServiceResult<CurrentUserResponse>> GetCurrentUserAsync(CancellationToken cancellationToken = default);
    Task<ServiceResult<CurrentUserResponse>> UpdatePreferredLanguageAsync(string? preferredLanguage, CancellationToken cancellationToken = default);
    /// <summary>Sets the first name (required) and last name (optional) of the current user; ADR-030 section 5.</summary>
    Task<ServiceResult<bool>> UpdateNameAsync(string firstName, string? lastName, CancellationToken cancellationToken = default);
    /// <summary>Sets or replaces the phone number of the current user; ADR-030 section 8.</summary>
    Task<ServiceResult<bool>> UpdatePhoneAsync(string phoneNumber, CancellationToken cancellationToken = default);
    Task<ServiceResult<bool>> ChangePasswordAsync(string currentPassword, string newPassword, CancellationToken cancellationToken = default);

    // Home-point model: the home point is the only source of a user's listings' location.
    Task<ServiceResult<CurrentUserResponse>> UpdateHomePointAsync(decimal latitude, decimal longitude, CancellationToken cancellationToken = default);
    Task<ServiceResult<CurrentUserResponse>> ClearHomePointAsync(CancellationToken cancellationToken = default);
}
