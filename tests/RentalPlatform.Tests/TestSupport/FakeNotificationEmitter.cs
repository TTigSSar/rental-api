using RentalPlatform.Application.Abstractions;
using RentalPlatform.Domain.Entities;

namespace RentalPlatform.Tests.TestSupport;

// No-op test double for the notification emitter. Emitting is a best-effort side
// effect, so tests of the core booking/moderation flows don't need it to do work.
//
// PickupAreaChangedAsync is the exception: the home-point tests assert on WHETHER it fired and for
// which bookings (the fan-out is gated on the public pair or district actually changing), so that
// one records its calls. Set ThrowOnPickupAreaChanged to prove a failing emitter cannot roll back
// the home-point move that already committed.
public sealed class FakeNotificationEmitter : INotificationEmitter
{
    public sealed record PickupAreaChangedCall(Booking Booking, User Owner, District? NewDistrict);

    public List<PickupAreaChangedCall> PickupAreaChangedCalls { get; } = new();

    public bool ThrowOnPickupAreaChanged { get; set; }

    // Fails for ONE booking and succeeds for the rest — the shape of the real defect, where a single
    // booking with an unloaded or deleted Listing threw before the emitter's own guard and took
    // every remaining renter's notification with it. ThrowOnPickupAreaChanged above fails for all of
    // them, which proves something different (the move still commits).
    public Guid? ThrowOnPickupAreaChangedForBookingId { get; set; }

    public Task PickupAreaChangedAsync(Booking booking, User owner, District? newDistrict, CancellationToken cancellationToken = default)
    {
        PickupAreaChangedCalls.Add(new PickupAreaChangedCall(booking, owner, newDistrict));

        if (ThrowOnPickupAreaChanged || ThrowOnPickupAreaChangedForBookingId == booking.Id)
        {
            throw new InvalidOperationException("Simulated notification failure.");
        }

        return Task.CompletedTask;
    }

    public Task BookingRequestedAsync(Booking booking, User renter, Listing listing, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task BookingApprovedAsync(Booking booking, User owner, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task BookingDeclinedAsync(Booking booking, User owner, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task ListingApprovedAsync(Listing listing, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task ListingRejectedAsync(Listing listing, string? reason, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
