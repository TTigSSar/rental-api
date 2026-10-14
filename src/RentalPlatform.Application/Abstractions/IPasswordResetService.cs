using RentalPlatform.Application.Common;

namespace RentalPlatform.Application.Abstractions;

public interface IPasswordResetService
{
    /// <summary>
    /// Always succeeds for the caller (202) unless the production email gate is closed
    /// (<c>auth.password_reset_unavailable</c>); sends only when every rule allows it.
    /// </summary>
    Task<ServiceResult<bool>> ForgotAsync(string? email, string? requestLanguage, CancellationToken cancellationToken = default);

    /// <summary>Read-only liveness check of a reset token; never consumes or revokes.</summary>
    Task<ServiceResult<bool>> CheckAsync(string token, CancellationToken cancellationToken = default);

    /// <summary>Validates the token and the new password, commits. Returns the user's id.</summary>
    Task<ServiceResult<Guid>> ResetAsync(string token, string newPassword, CancellationToken cancellationToken = default);
}
