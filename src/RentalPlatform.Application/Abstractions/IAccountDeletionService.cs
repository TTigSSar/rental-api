using RentalPlatform.Application.Common;
using RentalPlatform.Application.DTOs;

namespace RentalPlatform.Application.Abstractions;

public interface IAccountDeletionService
{
    /// <summary>
    /// ADR-056: anonymizes the current user's account. Success means the deletion is committed and the
    /// user's sessions are revoked. Errors: account.invalid_confirmation, account.admin_cannot_delete,
    /// account.has_open_rentals (with the <c>openRentals</c> extension), account.state_changed,
    /// auth.unauthenticated. Blocked users are NOT refused (amends ADR-030 section 11).
    /// </summary>
    Task<ServiceResult<bool>> DeleteCurrentUserAsync(DeleteAccountRequest request, CancellationToken cancellationToken = default);
}
