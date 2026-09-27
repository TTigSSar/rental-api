using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Application.Abstractions;

/// <summary>An inbox row: a conversation plus the current user's view of it.</summary>
/// <param name="Booking">
/// The linked booking. Null for a Moderation conversation (<see cref="Conversation.Kind"/> ==
/// <see cref="ConversationKind.Moderation"/>), which has none.
/// </param>
/// <param name="LastMessageSenderId">
/// Sender of the conversation's last message. Null when there is no last message, or it was a
/// system message (system messages have a null sender).
/// </param>
/// <param name="LastMessageType">
/// Type of the conversation's last message. Null when there is no last message yet.
/// </param>
public sealed record ChatConversationListItem(
    Conversation Conversation,
    User Counterpart,
    Booking? Booking,
    int UnreadCount,
    Guid? LastMessageSenderId,
    MessageType? LastMessageType = null);

/// <summary>
/// Full conversation view: the thread, its context, and a page of messages.
/// <paramref name="Booking"/> is null for a Moderation conversation.
/// </summary>
public sealed record ChatConversationDetails(
    Conversation Conversation,
    User Counterpart,
    Booking? Booking,
    DateTime? CounterpartLastReadAt,
    IReadOnlyList<ChatMessage> Messages);

/// <summary>"all" | "unread" | "needsReply" filter for the admin Messages thread queue.</summary>
public enum ModerationThreadFilter
{
    All,
    Unread,
    NeedsReply
}

/// <summary>
/// Filter-independent totals over the same (Kind == Moderation, search-filtered) set
/// ListModerationThreadsAsync lists — same convention as ReportStatusCounts/AdminUserStatusCounts:
/// stable as the admin switches the All/Unread/NeedsReply pill with the same search term applied.
/// <see cref="Unread"/> and <see cref="NeedsReply"/> use the exact same predicates as
/// ModerationThreadFilter.Unread/NeedsReply in ListModerationThreadsAsync.
/// </summary>
public sealed record ModerationThreadCounts(int All, int Unread, int NeedsReply);

/// <summary>One page of the admin Messages thread queue, plus the total count of the filtered set.</summary>
public sealed record ModerationThreadsPage(IReadOnlyCollection<ModerationThreadRow> Items, int TotalCount, ModerationThreadCounts Counts);

/// <summary>
/// One row of the admin Messages thread queue: a Moderation conversation joined to its member.
/// Status/MarketplaceRole are derived by the service from <see cref="MemberIsBlocked"/>/
/// <see cref="MemberIsIdConfirmed"/>/<see cref="MemberListingCount"/>/<see cref="MemberRentalCount"/>
/// — same convention as AdminUsersStore.AdminUserRow — not by this store.
/// </summary>
/// <param name="LastMessageType">
/// Type of the conversation's last message, resolved the same way ChatConversationListItem
/// resolves it query-time. Null when there is no last message yet.
/// </param>
/// <param name="LastMessageNoteSubject">
/// <see cref="ChatMessage.NoteSubject"/> of the last message when
/// <see cref="LastMessageType"/> is <see cref="MessageType.ModerationNote"/>; null otherwise (including
/// when there is no last message yet). <see cref="ModerationThreadRow.LastMessageSnippet"/> is the raw
/// note body in that case — this lets the client render "Note: {subject}" instead of bare body text.
/// </param>
public sealed record ModerationThreadRow(
    Guid ConversationId,
    Guid MemberId,
    string MemberFirstName,
    string MemberLastName,
    string? MemberAvatarUrl,
    bool MemberIsBlocked,
    bool MemberIsIdConfirmed,
    int MemberListingCount,
    int MemberRentalCount,
    int MemberOpenFlagCount,
    int UnreadCount,
    string? LastMessageSnippet,
    DateTime? LastMessageAt,
    bool LastMessageFromMember,
    MessageType? LastMessageType,
    string? LastMessageNoteSubject,
    DateTime CreatedAt);

public interface IConversationsStore
{
    Task<User?> FindUserByIdAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the conversation for a booking, creating it (and the two participant rows) on
    /// first access. Returns null when the booking does not exist or <paramref name="currentUserId"/>
    /// is neither its owner nor its renter.
    /// </summary>
    Task<Conversation?> GetOrCreateForBookingAsync(Guid bookingId, Guid currentUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// System-context get-or-create: returns the conversation for a booking, creating it (and the
    /// two participant rows) on first access, WITHOUT a caller-participant check — used by booking
    /// lifecycle events (see <see cref="IChatSystemMessageEmitter"/>), which are authoritative and
    /// have no acting "current user". Null only when the booking does not exist.
    /// </summary>
    Task<Conversation?> GetOrCreateForBookingSystemAsync(Guid bookingId, CancellationToken cancellationToken = default);

    /// <summary>Conversations where the user is owner or renter, newest activity first (nulls last).</summary>
    Task<IReadOnlyList<ChatConversationListItem>> ListForUserAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Conversation with a page of messages (newest page first). Null when it does not exist.</summary>
    Task<ChatConversationDetails?> GetDetailsAsync(
        Guid conversationId,
        Guid userId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    /// <summary>Loads a conversation without its messages (participant/closed checks). Null when absent.</summary>
    Task<Conversation?> FindByIdAsync(Guid conversationId, CancellationToken cancellationToken = default);

    /// <summary>Inserts a Text message and refreshes the conversation's denormalised preview fields.</summary>
    Task<ChatMessage> AddTextMessageAsync(Guid conversationId, Guid senderId, string content, CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts an Image message (with its caption and stored attachment URL) and refreshes the
    /// conversation's denormalised preview fields. <paramref name="caption"/> becomes
    /// <c>LastMessageSnippet</c> when present (truncated like the text path); when absent,
    /// <c>LastMessageSnippet</c> is set to null so the client renders its own localized
    /// "photo" placeholder off <c>ChatConversationResponse.LastMessageType</c> rather than the
    /// server baking in a non-localizable string.
    /// </summary>
    Task<ChatMessage> AddImageMessageAsync(
        Guid conversationId,
        Guid senderId,
        string? caption,
        string attachmentUrl,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts a System message (null sender) for the given kind and refreshes the conversation's
    /// denormalised preview fields, unless a System message of that same kind already exists in
    /// this conversation — a booking transition fires once, but the emit is made idempotent against
    /// retries. Returns null when skipped as a duplicate.
    /// </summary>
    Task<ChatMessage?> AddSystemMessageAsync(Guid conversationId, ChatSystemKind kind, string body, CancellationToken cancellationToken = default);

    /// <summary>Advances this participant's read cursor to the latest message. False when not a participant.</summary>
    Task<bool> MarkReadAsync(Guid conversationId, Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get-or-create for the admin Messages screen: returns the single Moderation conversation for
    /// this member (<c>Kind == Moderation &amp;&amp; RenterId == memberId</c>), creating it (with
    /// <paramref name="moderatorId"/> as <c>OwnerId</c> — see <see cref="Conversation"/>'s doc
    /// comment) on first access. One thread per member, not per moderator: a second moderator
    /// calling this for the same member gets back the same conversation instead of a duplicate.
    /// </summary>
    Task<Conversation> GetOrCreateForModerationAsync(Guid moderatorId, Guid memberId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts a ModerationNote message (<see cref="MessageType.ModerationNote"/>) authored by
    /// <paramref name="moderatorId"/> and refreshes the conversation's denormalised preview
    /// fields — a distinct insert path from <see cref="AddSystemMessageAsync"/> with
    /// <b>no idempotency guard</b> (unlike a booking-lifecycle System line, a moderation note is
    /// never a retry of "the same event"; two rejections of two different listings must both post).
    /// </summary>
    Task<ChatMessage> AddModerationNoteAsync(
        Guid conversationId,
        Guid moderatorId,
        ModerationNoteKind kind,
        string? subject,
        string? reason,
        string body,
        CancellationToken cancellationToken = default);

    /// <summary>One page of the admin Messages thread queue (filter/search/paging applied), newest activity first.</summary>
    Task<ModerationThreadsPage> ListModerationThreadsAsync(
        ModerationThreadFilter filter,
        string? search,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    /// <summary>Single-row equivalent of a <see cref="ModerationThreadRow"/> for one conversation. Null if not a Moderation conversation.</summary>
    Task<ModerationThreadRow?> GetModerationThreadRowAsync(Guid conversationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Closes the booking's conversation (sets <c>ClosedAt</c>) for the read-only chat lock — per
    /// ADR-001, triggered once a booking is Completed AND both party reviews (owner review + renter
    /// review) are in. Idempotent: a no-op when the conversation does not exist or is already closed.
    /// Best-effort like <see cref="IChatSystemMessageEmitter"/>: implementations MUST NOT throw —
    /// failures are logged internally and swallowed so this can never break review submission or
    /// booking completion. Returns true only when this call is the one that closed it.
    /// </summary>
    Task<bool> CloseForBookingAsync(Guid bookingId, DateTime closedAtUtc, CancellationToken cancellationToken = default);
}
