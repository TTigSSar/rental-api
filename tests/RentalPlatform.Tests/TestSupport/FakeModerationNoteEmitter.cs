using RentalPlatform.Application.Abstractions;

namespace RentalPlatform.Tests.TestSupport;

// No-op (but recording) test double for the moderation note emitter. Emitting is a best-effort
// side effect (see IModerationNoteEmitter), so most AdminListingsService/AdminUsersService tests
// don't need it to do anything beyond not throwing — the recorded calls let a few tests assert a
// note fired with the right kind/subject/reason.
public sealed class FakeModerationNoteEmitter : IModerationNoteEmitter
{
    public List<(Guid ModeratorId, Guid MemberId, string ListingTitle, string ReasonLabel, string? Note)> ListingRejectedCalls { get; } = new();
    public List<(Guid ModeratorId, Guid MemberId, string ListingTitle, string FromCategoryName, string ToCategoryName)> ListingRecategorisedCalls { get; } = new();
    public List<(Guid ModeratorId, Guid MemberId, string? Reason)> AccountSuspendedCalls { get; } = new();

    public Task ListingRejectedAsync(
        Guid moderatorId, Guid memberId, string listingTitle, string reasonLabel, string? note, CancellationToken cancellationToken = default)
    {
        ListingRejectedCalls.Add((moderatorId, memberId, listingTitle, reasonLabel, note));
        return Task.CompletedTask;
    }

    public Task ListingRecategorisedAsync(
        Guid moderatorId, Guid memberId, string listingTitle, string fromCategoryName, string toCategoryName, CancellationToken cancellationToken = default)
    {
        ListingRecategorisedCalls.Add((moderatorId, memberId, listingTitle, fromCategoryName, toCategoryName));
        return Task.CompletedTask;
    }

    public Task AccountSuspendedAsync(
        Guid moderatorId, Guid memberId, string? reason, CancellationToken cancellationToken = default)
    {
        AccountSuspendedCalls.Add((moderatorId, memberId, reason));
        return Task.CompletedTask;
    }
}
