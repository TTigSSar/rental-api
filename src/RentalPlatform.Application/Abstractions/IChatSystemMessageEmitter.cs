using RentalPlatform.Domain.Entities;

namespace RentalPlatform.Application.Abstractions;

/// <summary>
/// Emits a System <see cref="ChatMessage"/> into a booking's conversation when the booking's
/// lifecycle transitions. Every method is best-effort: an emit failure is logged and swallowed
/// so it can never break the booking action (create/approve/handover/complete) that triggered it.
/// <c>actorUserId</c> is the user whose action caused the line (RC-5): the line is not unread for them.
/// Null for a system/timer transition, which counts as unread for both parties.
/// </summary>
public interface IChatSystemMessageEmitter
{
    /// <summary>A renter requested a booking → "Booking requested." system line.</summary>
    Task BookingRequestedAsync(Booking booking, Guid? actorUserId, CancellationToken cancellationToken = default);

    /// <summary>The owner approved a booking → "The owner approved the request." system line.</summary>
    Task BookingApprovedAsync(Booking booking, Guid? actorUserId, CancellationToken cancellationToken = default);

    /// <summary>The owner marked the toy handed over (Approved → Active) → "Toy handed over…" system line.</summary>
    Task BookingHandedOverAsync(Booking booking, Guid? actorUserId, CancellationToken cancellationToken = default);

    /// <summary>The rental completed → "The rental is complete." system line.</summary>
    Task BookingCompletedAsync(Booking booking, Guid? actorUserId, CancellationToken cancellationToken = default);

    /// <summary>The owner declined the request → "declined" system line (carries <c>booking.RejectionReason</c> when set).</summary>
    Task BookingRejectedAsync(Booking booking, Guid? actorUserId, CancellationToken cancellationToken = default);

    /// <summary>The renter cancelled the booking → "cancelled" system line.</summary>
    Task BookingCancelledAsync(Booking booking, Guid? actorUserId, CancellationToken cancellationToken = default);
}
