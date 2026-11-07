namespace RentalPlatform.Application.DTOs;

/// <summary>
/// One Approved/Active booking that blocks account deletion (ADR-056). Carried in the
/// <c>openRentals</c> ProblemDetails extension of 409 <c>account.has_open_rentals</c>.
/// <see cref="Role"/> is "renter" or "owner" from the deleting user's point of view.
/// </summary>
public sealed class OpenRentalResponse
{
    public Guid BookingId { get; init; }
    public string ListingTitle { get; init; } = string.Empty;
    public string? ListingImageUrl { get; init; }
    public DateOnly StartDate { get; init; }
    public DateOnly EndDate { get; init; }
    public string Role { get; init; } = string.Empty;
}
