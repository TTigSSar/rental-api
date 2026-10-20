using RentalPlatform.Application.Abstractions;
using RentalPlatform.Domain.Entities;

namespace RentalPlatform.Tests.TestSupport;

// No-op test double for the chat system-message emitter. Emitting is a best-effort side
// effect, so tests of the core booking lifecycle don't need it to do work.
public sealed class FakeChatSystemMessageEmitter : IChatSystemMessageEmitter
{
    public List<string> Calls { get; } = new();

    public Task BookingRejectedAsync(Booking booking, Guid? actorUserId, CancellationToken cancellationToken = default)
    {
        Calls.Add("rejected");
        return Task.CompletedTask;
    }

    public Task BookingCancelledAsync(Booking booking, Guid? actorUserId, CancellationToken cancellationToken = default)
    {
        Calls.Add("cancelled");
        return Task.CompletedTask;
    }

    public Task BookingRequestedAsync(Booking booking, Guid? actorUserId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task BookingApprovedAsync(Booking booking, Guid? actorUserId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task BookingHandedOverAsync(Booking booking, Guid? actorUserId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task BookingCompletedAsync(Booking booking, Guid? actorUserId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
