using RentalPlatform.Domain.Entities;

namespace RentalPlatform.Application.Abstractions;

/// <summary>
/// Emits notifications when domain events fire. Every method is best-effort: an
/// emit failure is logged and swallowed so it can never break the core action
/// (creating a booking, moderating a listing, …) that triggered it.
/// </summary>
public interface INotificationEmitter
{
    /// <summary>A renter requested a booking → notify the listing owner.</summary>
    Task BookingRequestedAsync(Booking booking, User renter, Listing listing, CancellationToken cancellationToken = default);

    /// <summary>The owner approved a booking → notify the renter.</summary>
    Task BookingApprovedAsync(Booking booking, User owner, CancellationToken cancellationToken = default);

    /// <summary>The owner declined a booking → notify the renter.</summary>
    Task BookingDeclinedAsync(Booking booking, User owner, CancellationToken cancellationToken = default);

    /// <summary>A listing passed moderation → notify the owner.</summary>
    Task ListingApprovedAsync(Listing listing, CancellationToken cancellationToken = default);

    /// <summary>A listing was sent back by moderation → notify the owner.</summary>
    Task ListingRejectedAsync(Listing listing, string? reason, CancellationToken cancellationToken = default);
}
