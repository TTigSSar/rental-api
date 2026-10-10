using RentalPlatform.Application.Common;
using RentalPlatform.Domain.Entities;

namespace RentalPlatform.Application.Abstractions;

public interface IEmailVerificationService
{
    /// <summary>False when the production email gate is closed (ADR-028 §9).</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Creates a pending registration, or replaces an existing unverified one (D2), then sends the
    /// link. <paramref name="candidate"/> carries the final field values (hash already computed).
    /// </summary>
    Task<ServiceResult<bool>> RegisterPendingAsync(
        User candidate, decimal? homeLatitude, decimal? homeLongitude, CancellationToken cancellationToken = default);

    /// <summary>Always succeeds for the caller (202) unless the gate is closed; sends at most when allowed.</summary>
    Task<ServiceResult<bool>> ResendAsync(string email, CancellationToken cancellationToken = default);

    /// <summary>Checks the token and the password and commits. Returns the verified user's id.</summary>
    Task<ServiceResult<Guid>> VerifyAsync(string token, string password, CancellationToken cancellationToken = default);
}
