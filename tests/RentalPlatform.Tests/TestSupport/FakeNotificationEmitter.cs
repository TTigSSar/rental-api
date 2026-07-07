using RentalPlatform.Application.Abstractions;
using RentalPlatform.Domain.Entities;

namespace RentalPlatform.Tests.TestSupport;

// No-op test double for the notification emitter. Emitting is a best-effort side
// effect, so tests of the core booking/moderation flows don't need it to do work.
public sealed class FakeNotificationEmitter : INotificationEmitter
{
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
