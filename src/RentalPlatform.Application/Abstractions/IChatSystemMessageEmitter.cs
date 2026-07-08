using RentalPlatform.Domain.Entities;

namespace RentalPlatform.Application.Abstractions;

/// <summary>
/// Emits a System <see cref="ChatMessage"/> into a booking's conversation when the booking's
/// lifecycle transitions. Every method is best-effort: an emit failure is logged and swallowed
/// so it can never break the booking action (create/approve/handover/complete) that triggered it.
/// </summary>
public interface IChatSystemMessageEmitter
{
    /// <summary>A renter requested a booking → "Booking requested." system line.</summary>
    Task BookingRequestedAsync(Booking booking, CancellationToken cancellationToken = default);

    /// <summary>The owner approved a booking → "The owner approved the request." system line.</summary>
    Task BookingApprovedAsync(Booking booking, CancellationToken cancellationToken = default);

    /// <summary>The owner marked the toy handed over (Approved → Active) → "Toy handed over…" system line.</summary>
    Task BookingHandedOverAsync(Booking booking, CancellationToken cancellationToken = default);

    /// <summary>The rental completed → "The rental is complete." system line.</summary>
    Task BookingCompletedAsync(Booking booking, CancellationToken cancellationToken = default);
}
