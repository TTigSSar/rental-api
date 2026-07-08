using RentalPlatform.Application.Abstractions;
using RentalPlatform.Domain.Entities;

namespace RentalPlatform.Tests.TestSupport;

// No-op test double for the chat system-message emitter. Emitting is a best-effort side
// effect, so tests of the core booking lifecycle don't need it to do work.
public sealed class FakeChatSystemMessageEmitter : IChatSystemMessageEmitter
{
    public Task BookingRequestedAsync(Booking booking, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task BookingApprovedAsync(Booking booking, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task BookingHandedOverAsync(Booking booking, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task BookingCompletedAsync(Booking booking, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
