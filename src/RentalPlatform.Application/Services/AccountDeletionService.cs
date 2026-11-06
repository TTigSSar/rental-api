using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.Common;
using RentalPlatform.Application.DTOs;
using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Application.Services;

// Self-service account deletion (ADR-056): the row stays and is anonymized, personal data is erased,
// sessions end, a confirmation email is sent. The ORDER is the contract (spec Behaviour 1-3):
// conditional lock of the user row -> admin -> confirmation -> open rentals -> close Pending -> erase
// -> commit, all in ONE transaction owned by the store session (any refusal rolls back and changes
// nothing); after the commit: revoke sessions, tell the counterparties, delete the avatar file, send
// the email. Nothing after the commit can fail the request.
//
// Blocked users are deliberately NOT refused (amends ADR-030 section 11): Google Play requires the path.
public sealed class AccountDeletionService : IAccountDeletionService
{
    public static class ErrorCodes
    {
        public const string Unauthenticated = "auth.unauthenticated";
        public const string InvalidConfirmation = "account.invalid_confirmation";
        public const string AdminCannotDelete = "account.admin_cannot_delete";
        public const string HasOpenRentals = "account.has_open_rentals";
        public const string StateChanged = "account.state_changed";
    }

    /// <summary>Stored in Report/ModerationLogEntry TargetLabel (admin-facing, English).</summary>
    public const string DeletedUserLabel = "Deleted user";

    private readonly ICurrentUserContext _currentUserContext;
    private readonly IAccountDeletionStore _store;
    private readonly IPasswordHasher _passwordHasher;
    private readonly INotificationEmitter _notificationEmitter;
    private readonly IChatSystemMessageEmitter _chatSystemMessageEmitter;
    private readonly ISessionRevoker _sessionRevoker;
    private readonly IFileStorageService _fileStorage;
    private readonly IEmailService _emailService;
    private readonly IEmailSendBudget _emailBudget;
    private readonly TimeProvider _timeProvider;

    public AccountDeletionService(
        ICurrentUserContext currentUserContext,
        IAccountDeletionStore store,
        IPasswordHasher passwordHasher,
        INotificationEmitter notificationEmitter,
        IChatSystemMessageEmitter chatSystemMessageEmitter,
        ISessionRevoker sessionRevoker,
        IFileStorageService fileStorage,
        IEmailService emailService,
        IEmailSendBudget emailBudget,
        TimeProvider timeProvider)
    {
        _currentUserContext = currentUserContext;
        _store = store;
        _passwordHasher = passwordHasher;
        _notificationEmitter = notificationEmitter;
        _chatSystemMessageEmitter = chatSystemMessageEmitter;
        _sessionRevoker = sessionRevoker;
        _fileStorage = fileStorage;
        _emailService = emailService;
        _emailBudget = emailBudget;
        _timeProvider = timeProvider;
    }

    public async Task<ServiceResult<bool>> DeleteCurrentUserAsync(
        DeleteAccountRequest request, CancellationToken cancellationToken = default)
    {
        if (_currentUserContext.UserId is not { } userId)
        {
            return Fail(ErrorCodes.Unauthenticated, "Current user is not authenticated.");
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        AccountSnapshot account;
        IReadOnlyList<ClosedBooking> closed;

        // 1. Lock. A missing or already-deleted row is an unauthenticated caller, like any other write.
        var session = await _store.BeginAsync(userId, cancellationToken);
        if (session is null)
        {
            return Fail(ErrorCodes.Unauthenticated, "Current user is not authenticated.");
        }

        await using (session)
        {
            account = session.Account;

            // 2. Admin.
            if (account.Role == UserRole.Admin)
            {
                return Fail(ErrorCodes.AdminCannotDelete, "Administrators cannot delete their own account.");
            }

            // 3. Confirmation.
            if (!IsConfirmed(account, request))
            {
                return Fail(ErrorCodes.InvalidConfirmation, "The confirmation does not match.");
            }

            // 4. Open rentals.
            var open = await session.FindOpenRentalsAsync(cancellationToken);
            if (open.Count > 0)
            {
                return ServiceResult<bool>.Failure(new ServiceError
                {
                    Code = ErrorCodes.HasOpenRentals,
                    Message = "Finish your open rentals before deleting the account.",
                    Extensions = new Dictionary<string, object?>
                    {
                        ["openRentals"] = open.Select(rental => new OpenRentalResponse
                        {
                            BookingId = rental.BookingId,
                            ListingTitle = rental.ListingTitle,
                            ListingImageUrl = rental.ListingImageUrl,
                            StartDate = rental.StartDate,
                            EndDate = rental.EndDate,
                            Role = rental.Role
                        }).ToList()
                    }
                });
            }

            // 5. Close Pending (CAS each); a lost CAS leaves nothing behind (the session rolls back on dispose).
            var closedOrNull = await session.ClosePendingBookingsAsync(now, cancellationToken);
            if (closedOrNull is null)
            {
                return Fail(ErrorCodes.StateChanged, "A booking changed while the account was being deleted. Please try again.");
            }

            closed = closedOrNull;

            // 6. Erase and commit.
            await session.EraseAsync(now, DeletedUserLabel, cancellationToken);
            await session.CommitAsync(cancellationToken);
        }

        await AfterCommitAsync(account, closed);
        return ServiceResult<bool>.Success(true);
    }

    private bool IsConfirmed(AccountSnapshot account, DeleteAccountRequest request)
    {
        if (PasswordPolicy.HasUsablePassword(account.PasswordHash))
        {
            return !string.IsNullOrEmpty(request.Password)
                   && _passwordHasher.VerifyPassword(request.Password, account.PasswordHash);
        }

        // No password (Google only): the typed email must equal the stored one. Ordinal equality of the
        // normalized strings, never the database collation (ADR-028 section 2, M-066).
        return !string.IsNullOrWhiteSpace(request.Email)
               && string.Equals(NormalizeEmail(account.Email), NormalizeEmail(request.Email), StringComparison.Ordinal);
    }

    // Everything here is best-effort and runs after the commit: the deletion has happened, so no failure
    // below may turn it into an error response.
    private async Task AfterCommitAsync(AccountSnapshot account, IReadOnlyList<ClosedBooking> closed)
    {
        // First: drop the cached security state and abort open chat connections (ADR-055). Never throws.
        await _sessionRevoker.RevokeAsync(account.Id);

        foreach (var item in closed)
        {
            try
            {
                if (item.Rejected)
                {
                    // The renter is told the request was closed; the actor is "Deleted user".
                    await _notificationEmitter.BookingDeclinedByDeletedOwnerAsync(item.Booking);
                    await _chatSystemMessageEmitter.BookingRejectedAsync(item.Booking, account.Id);
                }
                else
                {
                    await _chatSystemMessageEmitter.BookingCancelledAsync(item.Booking, account.Id);
                }
            }
            catch
            {
                // The emitters log and swallow their own failures; this guards a throwing double or a future change.
            }
        }

        if (!string.IsNullOrWhiteSpace(account.AvatarUrl))
        {
            try
            {
                // Removes a locally stored file; a remote URL (Google avatar) is left alone by the storage.
                await _fileStorage.DeleteListingImageAsync(account.AvatarUrl);
            }
            catch
            {
                // The application layer has no logger; the file is orphaned, the account is gone.
            }
        }

        try
        {
            // Counts against the shared send budget (ADR-029). CancellationToken.None: the row is committed,
            // so a client that disconnected must not also cost the user their email.
            if (_emailBudget.TryAcquire())
            {
                await _emailService.SendAccountDeletedAsync(account.Email, account.PreferredLanguage, CancellationToken.None);
            }
        }
        catch
        {
            // IEmailService never throws and logs its own failures; this only guards a misbehaving implementation.
        }
    }

    private static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    private static ServiceResult<bool> Fail(string code, string message) =>
        ServiceResult<bool>.Failure(new ServiceError { Code = code, Message = message });
}
