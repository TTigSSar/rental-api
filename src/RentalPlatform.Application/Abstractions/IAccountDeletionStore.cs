using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Application.Abstractions;

/// <summary>What the service needs to know about the account it is deleting, read after the row lock.</summary>
public sealed record AccountSnapshot(
    Guid Id,
    string Email,
    string PasswordHash,
    string? PreferredLanguage,
    string? AvatarUrl,
    UserRole Role);

/// <summary>An Approved/Active booking that blocks the deletion. <see cref="Role"/> is "renter" or "owner".</summary>
public sealed record OpenRental(
    Guid BookingId,
    string ListingTitle,
    string? ListingImageUrl,
    DateOnly StartDate,
    DateOnly EndDate,
    string Role);

/// <summary>
/// A Pending booking the deletion closed. <see cref="Booking"/> carries its new status and is loaded with
/// Listing (+ Images, Owner) and Renter for the post-commit notification and chat line.
/// <see cref="Rejected"/>: the deleted user was the owner (incoming request, Rejected); otherwise the
/// deleted user was the renter (outgoing request, Cancelled).
/// </summary>
public sealed record ClosedBooking(Booking Booking, bool Rejected);

/// <summary>
/// ADR-056 persistence. Lock order is Users first, as in ADR-029. All the writes of one deletion live in
/// one transaction owned by the <see cref="IAccountDeletionSession"/>.
/// </summary>
public interface IAccountDeletionStore
{
    /// <summary>
    /// Opens the transaction and locks the user row with a conditional
    /// <c>UPDATE Users ... WHERE Id=@id AND DeletedAt IS NULL</c>. Null (nothing is left open) when the row
    /// is missing or already deleted.
    /// </summary>
    Task<IAccountDeletionSession?> BeginAsync(Guid userId, CancellationToken cancellationToken = default);
}

/// <summary>
/// The open deletion transaction. Disposing without <see cref="CommitAsync"/> rolls everything back.
/// </summary>
public interface IAccountDeletionSession : IAsyncDisposable
{
    AccountSnapshot Account { get; }

    /// <summary>Approved/Active bookings where the user is the renter or the listing owner.</summary>
    Task<IReadOnlyList<OpenRental>> FindOpenRentalsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Closes every Pending booking of the user (outgoing -> Cancelled, incoming -> Rejected), each by a
    /// compare-and-set on Status = Pending. Null when a booking left Pending in the meantime (a lost CAS):
    /// the caller must roll back.
    /// </summary>
    Task<IReadOnlyList<ClosedBooking>?> ClosePendingBookingsAsync(DateTime now, CancellationToken cancellationToken = default);

    /// <summary>
    /// Erases the user's data as listed in ADR-056 (and its amendments) and anonymizes the row:
    /// DeletedAt = <paramref name="now"/>, TokenVersion + 1.
    /// </summary>
    Task EraseAsync(DateTime now, string deletedUserLabel, CancellationToken cancellationToken = default);

    Task CommitAsync(CancellationToken cancellationToken = default);
}
