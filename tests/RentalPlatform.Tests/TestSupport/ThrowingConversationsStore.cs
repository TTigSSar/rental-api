using RentalPlatform.Application.Abstractions;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Tests.TestSupport;

// Failure-injection test double for IConversationsStore: every member throws. Used to prove
// ModerationNoteEmitter's try/catch-swallow doctrine — a note failure must never propagate to
// (and so never fail) the moderation action that triggered it.
public sealed class ThrowingConversationsStore : IConversationsStore
{
    private static InvalidOperationException Fail() => new("Simulated store failure.");

    public Task<User?> FindUserByIdAsync(Guid userId, CancellationToken cancellationToken = default) => throw Fail();

    public Task<Conversation?> GetOrCreateForBookingAsync(Guid bookingId, Guid currentUserId, CancellationToken cancellationToken = default) => throw Fail();

    public Task<Conversation?> GetOrCreateForBookingSystemAsync(Guid bookingId, CancellationToken cancellationToken = default) => throw Fail();

    public Task<IReadOnlyList<ChatConversationListItem>> ListForUserAsync(Guid userId, CancellationToken cancellationToken = default) => throw Fail();

    public Task<ChatConversationDetails?> GetDetailsAsync(Guid conversationId, Guid userId, int page, int pageSize, CancellationToken cancellationToken = default) => throw Fail();

    public Task<Conversation?> FindByIdAsync(Guid conversationId, CancellationToken cancellationToken = default) => throw Fail();

    public Task<ChatMessage> AddTextMessageAsync(Guid conversationId, Guid senderId, string content, CancellationToken cancellationToken = default) => throw Fail();

    public Task<ChatMessage> AddImageMessageAsync(Guid conversationId, Guid senderId, string? caption, string attachmentUrl, CancellationToken cancellationToken = default) => throw Fail();

    public Task<ChatMessage?> AddSystemMessageAsync(Guid conversationId, ChatSystemKind kind, string body, CancellationToken cancellationToken = default) => throw Fail();

    public Task<bool> MarkReadAsync(Guid conversationId, Guid userId, CancellationToken cancellationToken = default) => throw Fail();

    public Task<Conversation> GetOrCreateForModerationAsync(Guid moderatorId, Guid memberId, CancellationToken cancellationToken = default) => throw Fail();

    public Task<ChatMessage> AddModerationNoteAsync(Guid conversationId, Guid moderatorId, ModerationNoteKind kind, string? subject, string? reason, string body, CancellationToken cancellationToken = default) => throw Fail();

    public Task<ModerationThreadsPage> ListModerationThreadsAsync(ModerationThreadFilter filter, string? search, int page, int pageSize, CancellationToken cancellationToken = default) => throw Fail();

    public Task<ModerationThreadRow?> GetModerationThreadRowAsync(Guid conversationId, CancellationToken cancellationToken = default) => throw Fail();

    public Task<bool> CloseForBookingAsync(Guid bookingId, DateTime closedAtUtc, CancellationToken cancellationToken = default) => throw Fail();
}
