using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Infrastructure.Persistence;

public sealed class ConversationsStore : IConversationsStore
{
    private const int SnippetMaxLength = 500;

    // Projection-only shape for ListForUserAsync's last-message lookup (sender + type),
    // resolved query-time since Conversation only denormalises the snippet/timestamp.
    private sealed record LastMessageInfo(Guid Id, Guid? SenderId, MessageType Type);

    // Projection-only shape for BuildModerationThreadRowsAsync's per-conversation message scan.
    // Type/NoteSubject are carried so the last message's row can expose LastMessageType and (for a
    // ModerationNote) LastMessageNoteSubject without a second per-conversation query.
    private sealed record ModerationMessageInfo(Guid Id, Guid ConversationId, Guid? SenderId, MessageType Type, string? NoteSubject, DateTime CreatedAt);

    private readonly AppDbContext _dbContext;
    private readonly ILogger<ConversationsStore> _logger;

    public ConversationsStore(AppDbContext dbContext, ILogger<ConversationsStore> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public Task<User?> FindUserByIdAsync(Guid userId, CancellationToken cancellationToken = default) =>
        _dbContext.Users.FirstOrDefaultAsync(user => user.Id == userId, cancellationToken);

    public async Task<Conversation?> GetOrCreateForBookingAsync(
        Guid bookingId,
        Guid currentUserId,
        CancellationToken cancellationToken = default)
    {
        var existing = await _dbContext.Conversations
            .FirstOrDefaultAsync(conversation => conversation.BookingId == bookingId, cancellationToken);

        var booking = await _dbContext.Bookings
            .Include(entity => entity.Listing)
                .ThenInclude(listing => listing.Images)
            .FirstOrDefaultAsync(entity => entity.Id == bookingId, cancellationToken);

        if (booking is null)
        {
            return null;
        }

        var ownerId = booking.Listing.OwnerId;
        var renterId = booking.RenterId;

        // Only the two participants of this booking may open the thread.
        if (currentUserId != ownerId && currentUserId != renterId)
        {
            return null;
        }

        return existing ?? await CreateConversationAsync(booking, cancellationToken);
    }

    public async Task<Conversation?> GetOrCreateForBookingSystemAsync(
        Guid bookingId,
        CancellationToken cancellationToken = default)
    {
        var existing = await _dbContext.Conversations
            .FirstOrDefaultAsync(conversation => conversation.BookingId == bookingId, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var booking = await _dbContext.Bookings
            .Include(entity => entity.Listing)
                .ThenInclude(listing => listing.Images)
            .FirstOrDefaultAsync(entity => entity.Id == bookingId, cancellationToken);

        if (booking is null)
        {
            return null;
        }

        return await CreateConversationAsync(booking, cancellationToken);
    }

    // Shared by both get-or-create entry points once each has resolved (and, where relevant,
    // participant-checked) the booking: builds the conversation + its two participant rows.
    private async Task<Conversation> CreateConversationAsync(Booking booking, CancellationToken cancellationToken)
    {
        var ownerId = booking.Listing.OwnerId;
        var renterId = booking.RenterId;

        var primaryImage = booking.Listing.Images
            .OrderByDescending(image => image.IsPrimary)
            .ThenBy(image => image.SortOrder)
            .FirstOrDefault();

        var now = DateTime.UtcNow;
        var conversation = new Conversation
        {
            Id = Guid.NewGuid(),
            BookingId = booking.Id,
            Kind = ConversationKind.Booking,
            OwnerId = ownerId,
            RenterId = renterId,
            ToyTitle = booking.Listing.Title,
            ToyImageUrl = primaryImage?.Url,
            CreatedAt = now
        };

        conversation.Participants.Add(new ConversationParticipant
        {
            Id = Guid.NewGuid(),
            ConversationId = conversation.Id,
            UserId = ownerId
        });
        conversation.Participants.Add(new ConversationParticipant
        {
            Id = Guid.NewGuid(),
            ConversationId = conversation.Id,
            UserId = renterId
        });

        await _dbContext.Conversations.AddAsync(conversation, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return conversation;
    }

    public async Task<IReadOnlyList<ChatConversationListItem>> ListForUserAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var conversations = await _dbContext.Conversations
            .AsNoTracking()
            .Where(conversation => conversation.OwnerId == userId || conversation.RenterId == userId)
            .Include(conversation => conversation.Booking)
            .Include(conversation => conversation.Owner)
            .Include(conversation => conversation.Renter)
            // SQL Server sorts NULL lowest, so descending places conversations with no
            // activity yet (LastMessageAt == null) at the end.
            .OrderByDescending(conversation => conversation.LastMessageAt)
            .ThenByDescending(conversation => conversation.CreatedAt)
            .ToListAsync(cancellationToken);

        if (conversations.Count == 0)
        {
            return Array.Empty<ChatConversationListItem>();
        }

        var conversationIds = conversations.Select(conversation => conversation.Id).ToList();

        // One grouped query: messages the user has not read (after their own read cursor),
        // excluding their own messages, per conversation.
        var unreadCounts = await (
            from message in _dbContext.ChatMessages.AsNoTracking()
            join participant in _dbContext.ConversationParticipants.AsNoTracking()
                on message.ConversationId equals participant.ConversationId
            where participant.UserId == userId
                && conversationIds.Contains(message.ConversationId)
                && message.SenderId != userId
                && (participant.LastReadAt == null || message.CreatedAt > participant.LastReadAt)
            group message by message.ConversationId into grouped
            select new { ConversationId = grouped.Key, Count = grouped.Count() })
            .ToDictionaryAsync(row => row.ConversationId, row => row.Count, cancellationToken);

        // Resolve the last message's sender + type query-time (Conversation only denormalises
        // the snippet/timestamp) so the inbox can flag "You: ..." previews and pick an image
        // placeholder token without adding more denormalised columns.
        var lastMessageIds = conversations
            .Where(conversation => conversation.LastMessageId != null)
            .Select(conversation => conversation.LastMessageId!.Value)
            .ToList();

        var lastMessageInfos = lastMessageIds.Count == 0
            ? new Dictionary<Guid, LastMessageInfo>()
            : await _dbContext.ChatMessages
                .AsNoTracking()
                .Where(message => lastMessageIds.Contains(message.Id))
                .Select(message => new LastMessageInfo(message.Id, message.SenderId, message.Type))
                .ToDictionaryAsync(info => info.Id, cancellationToken);

        return conversations
            .Select(conversation =>
            {
                var counterpart = conversation.OwnerId == userId ? conversation.Renter : conversation.Owner;
                var unread = unreadCounts.TryGetValue(conversation.Id, out var count) ? count : 0;
                var lastMessageInfo = conversation.LastMessageId is { } lastMessageId
                    && lastMessageInfos.TryGetValue(lastMessageId, out var info)
                        ? info
                        : null;
                return new ChatConversationListItem(
                    conversation,
                    counterpart,
                    conversation.Booking,
                    unread,
                    lastMessageInfo?.SenderId,
                    lastMessageInfo?.Type);
            })
            .ToList();
    }

    public async Task<ChatConversationDetails?> GetDetailsAsync(
        Guid conversationId,
        Guid userId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var conversation = await _dbContext.Conversations
            .AsNoTracking()
            .Include(entity => entity.Booking)
            .Include(entity => entity.Owner)
            .Include(entity => entity.Renter)
            .FirstOrDefaultAsync(entity => entity.Id == conversationId, cancellationToken);

        if (conversation is null)
        {
            return null;
        }

        var counterpart = conversation.OwnerId == userId ? conversation.Renter : conversation.Owner;
        var counterpartId = conversation.OwnerId == userId ? conversation.RenterId : conversation.OwnerId;

        var counterpartLastReadAt = await _dbContext.ConversationParticipants
            .AsNoTracking()
            .Where(participant => participant.ConversationId == conversationId && participant.UserId == counterpartId)
            .Select(participant => participant.LastReadAt)
            .FirstOrDefaultAsync(cancellationToken);

        // Newest page first, then returned in chronological (ascending) order for rendering.
        var pageRows = await _dbContext.ChatMessages
            .AsNoTracking()
            .Where(message => message.ConversationId == conversationId)
            .OrderByDescending(message => message.CreatedAt)
            .ThenByDescending(message => message.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        pageRows.Reverse();

        return new ChatConversationDetails(conversation, counterpart, conversation.Booking, counterpartLastReadAt, pageRows);
    }

    public Task<Conversation?> FindByIdAsync(Guid conversationId, CancellationToken cancellationToken = default) =>
        _dbContext.Conversations.FirstOrDefaultAsync(conversation => conversation.Id == conversationId, cancellationToken);

    public async Task<ChatMessage> AddTextMessageAsync(
        Guid conversationId,
        Guid senderId,
        string content,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var message = new ChatMessage
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            SenderId = senderId,
            Type = MessageType.Text,
            Body = content,
            CreatedAt = now
        };

        await _dbContext.ChatMessages.AddAsync(message, cancellationToken);

        var conversation = await _dbContext.Conversations
            .FirstAsync(entity => entity.Id == conversationId, cancellationToken);
        conversation.LastMessageId = message.Id;
        conversation.LastMessageSnippet = content.Length > SnippetMaxLength
            ? content[..SnippetMaxLength]
            : content;
        conversation.LastMessageAt = now;

        await _dbContext.SaveChangesAsync(cancellationToken);

        return message;
    }

    public async Task<ChatMessage> AddImageMessageAsync(
        Guid conversationId,
        Guid senderId,
        string? caption,
        string attachmentUrl,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var message = new ChatMessage
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            SenderId = senderId,
            Type = MessageType.Image,
            Body = caption,
            AttachmentUrl = attachmentUrl,
            CreatedAt = now
        };

        await _dbContext.ChatMessages.AddAsync(message, cancellationToken);

        var conversation = await _dbContext.Conversations
            .FirstAsync(entity => entity.Id == conversationId, cancellationToken);
        conversation.LastMessageId = message.Id;
        // No text body to preview for a bare image: leave the snippet null so the client
        // renders its own localized placeholder off LastMessageType instead of the server
        // baking in a non-localizable string like "Photo" (see IConversationsStore doc).
        conversation.LastMessageSnippet = string.IsNullOrEmpty(caption)
            ? null
            : caption.Length > SnippetMaxLength
                ? caption[..SnippetMaxLength]
                : caption;
        conversation.LastMessageAt = now;

        await _dbContext.SaveChangesAsync(cancellationToken);

        return message;
    }

    public async Task<ChatMessage?> AddSystemMessageAsync(
        Guid conversationId,
        ChatSystemKind kind,
        string body,
        CancellationToken cancellationToken = default)
    {
        // Idempotency: a booking transition fires once, but guard against a retry re-inserting
        // the same system line by checking for an existing message of this kind in the thread.
        var alreadyExists = await _dbContext.ChatMessages
            .AnyAsync(
                message => message.ConversationId == conversationId
                    && message.Type == MessageType.System
                    && message.SystemKind == kind,
                cancellationToken);

        if (alreadyExists)
        {
            return null;
        }

        var now = DateTime.UtcNow;
        var message = new ChatMessage
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            SenderId = null,
            Type = MessageType.System,
            SystemKind = kind,
            Body = body,
            CreatedAt = now
        };

        await _dbContext.ChatMessages.AddAsync(message, cancellationToken);

        var conversation = await _dbContext.Conversations
            .FirstAsync(entity => entity.Id == conversationId, cancellationToken);
        conversation.LastMessageId = message.Id;
        conversation.LastMessageSnippet = body.Length > SnippetMaxLength
            ? body[..SnippetMaxLength]
            : body;
        conversation.LastMessageAt = now;

        await _dbContext.SaveChangesAsync(cancellationToken);

        return message;
    }

    public async Task<bool> MarkReadAsync(Guid conversationId, Guid userId, CancellationToken cancellationToken = default)
    {
        var participant = await _dbContext.ConversationParticipants
            .FirstOrDefaultAsync(
                entity => entity.ConversationId == conversationId && entity.UserId == userId,
                cancellationToken);

        if (participant is null)
        {
            return false;
        }

        var latest = await _dbContext.ChatMessages
            .AsNoTracking()
            .Where(message => message.ConversationId == conversationId)
            .OrderByDescending(message => message.CreatedAt)
            .ThenByDescending(message => message.Id)
            .Select(message => new { message.Id, message.CreatedAt })
            .FirstOrDefaultAsync(cancellationToken);

        if (latest is not null)
        {
            participant.LastReadMessageId = latest.Id;
            participant.LastReadAt = latest.CreatedAt;
        }
        else
        {
            participant.LastReadAt = DateTime.UtcNow;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<Conversation> GetOrCreateForModerationAsync(
        Guid moderatorId,
        Guid memberId,
        CancellationToken cancellationToken = default)
    {
        var existing = await FindModerationConversationAsync(memberId, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var now = DateTime.UtcNow;
        var conversation = new Conversation
        {
            Id = Guid.NewGuid(),
            BookingId = null,
            Kind = ConversationKind.Moderation,
            OwnerId = moderatorId,
            RenterId = memberId,
            ToyTitle = null,
            ToyImageUrl = null,
            CreatedAt = now
        };

        conversation.Participants.Add(new ConversationParticipant
        {
            Id = Guid.NewGuid(),
            ConversationId = conversation.Id,
            UserId = moderatorId
        });
        conversation.Participants.Add(new ConversationParticipant
        {
            Id = Guid.NewGuid(),
            ConversationId = conversation.Id,
            UserId = memberId
        });

        await _dbContext.Conversations.AddAsync(conversation, cancellationToken);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
            return conversation;
        }
        catch (DbUpdateException exception) when (IsUniqueConstraintViolation(exception))
        {
            // Lost the race: another call's insert won IX_Conversations_Kind_RenterId's filtered
            // unique index (Kind == Moderation) for this member first. Detach our half-inserted
            // graph — the transaction already rolled the failed INSERT back, so the tracker is
            // the only place it still exists — and hand back the winner's row instead.
            DetachAddedGraph(conversation);

            return await FindModerationConversationAsync(memberId, cancellationToken)
                ?? throw new InvalidOperationException(
                    $"Moderation conversation insert for member {memberId} hit a unique-constraint " +
                    "violation, but no existing row could be re-read.");
        }
    }

    private Task<Conversation?> FindModerationConversationAsync(Guid memberId, CancellationToken cancellationToken) =>
        _dbContext.Conversations
            .FirstOrDefaultAsync(
                conversation => conversation.Kind == ConversationKind.Moderation && conversation.RenterId == memberId,
                cancellationToken);

    private void DetachAddedGraph(Conversation conversation)
    {
        // Snapshot first: detaching a participant's entry triggers EF's relationship fixup,
        // which removes it from conversation.Participants — mutating the very collection a plain
        // foreach would still be enumerating.
        foreach (var participant in conversation.Participants.ToList())
        {
            _dbContext.Entry(participant).State = EntityState.Detached;
        }

        _dbContext.Entry(conversation).State = EntityState.Detached;
    }

    // Precise by design: only a genuine SQL Server unique/PK-violation (2601 = duplicate key on a
    // unique index, 2627 = duplicate key on a unique constraint) is treated as "we lost the race" —
    // mirrors FavoritesStore.TryAddAsync's identical check. Any other DbUpdateException (FK
    // violation, connection failure, etc.) is a genuine failure and must keep propagating.
    private static bool IsUniqueConstraintViolation(DbUpdateException exception) =>
        exception.InnerException is SqlException { Number: 2601 or 2627 };

    public async Task<ChatMessage> AddModerationNoteAsync(
        Guid conversationId,
        Guid moderatorId,
        ModerationNoteKind kind,
        string? subject,
        string? reason,
        string body,
        CancellationToken cancellationToken = default)
    {
        // No idempotency guard (unlike AddSystemMessageAsync): a moderation note is never a retry
        // of "the same event" — two rejections of two different listings both must post, and
        // de-duplicating on (ConversationId, NoteKind) would silently swallow the second one.
        var now = DateTime.UtcNow;
        var message = new ChatMessage
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            SenderId = moderatorId,
            Type = MessageType.ModerationNote,
            NoteKind = kind,
            NoteSubject = subject,
            NoteReason = reason,
            Body = body,
            CreatedAt = now
        };

        await _dbContext.ChatMessages.AddAsync(message, cancellationToken);

        var conversation = await _dbContext.Conversations
            .FirstAsync(entity => entity.Id == conversationId, cancellationToken);
        conversation.LastMessageId = message.Id;
        conversation.LastMessageSnippet = body.Length > SnippetMaxLength
            ? body[..SnippetMaxLength]
            : body;
        conversation.LastMessageAt = now;

        await _dbContext.SaveChangesAsync(cancellationToken);

        return message;
    }

    public async Task<ModerationThreadsPage> ListModerationThreadsAsync(
        ModerationThreadFilter filter,
        string? search,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        IQueryable<Conversation> query = _dbContext.Conversations
            .AsNoTracking()
            .Where(conversation => conversation.Kind == ConversationKind.Moderation)
            .Include(conversation => conversation.Renter);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(conversation =>
                conversation.Renter.FirstName.Contains(term) ||
                conversation.Renter.LastName.Contains(term) ||
                (conversation.Renter.FirstName + " " + conversation.Renter.LastName).Contains(term) ||
                conversation.Renter.Email.Contains(term));
        }

        // SQL Server sorts NULL lowest, so descending places conversations with no activity yet
        // (LastMessageAt == null) at the end — same convention as ListForUserAsync.
        var conversations = await query
            .OrderByDescending(conversation => conversation.LastMessageAt)
            .ThenByDescending(conversation => conversation.CreatedAt)
            .ToListAsync(cancellationToken);

        var rows = await BuildModerationThreadRowsAsync(conversations, cancellationToken);

        // Unread/needsReply are computed (not stored) per-conversation values, so the "unread"/
        // "needsReply" filters are applied here, after the batched computation, rather than
        // pushed into SQL — the moderation thread count (one per member ever contacted) is small
        // enough that this two-phase fetch-then-filter is simpler and still correct, following the
        // same batched-secondary-query convention ListForUserAsync/AdminReportsService.MapRowsAsync
        // use for other per-row derived values.
        //
        // Counts reuse these exact same two predicates over the full (search-filtered, filter-
        // independent) `rows` set computed above — no extra query, and no risk of the counts
        // disagreeing with what each pill's list actually returns.
        var counts = new ModerationThreadCounts(
            All: rows.Count,
            Unread: rows.Count(row => row.UnreadCount > 0),
            NeedsReply: rows.Count(row => row.LastMessageFromMember));

        IEnumerable<ModerationThreadRow> filteredRows = filter switch
        {
            ModerationThreadFilter.Unread => rows.Where(row => row.UnreadCount > 0),
            ModerationThreadFilter.NeedsReply => rows.Where(row => row.LastMessageFromMember),
            _ => rows
        };
        var filtered = filteredRows.ToList();

        var totalCount = filtered.Count;
        var pageItems = filtered
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        return new ModerationThreadsPage(pageItems, totalCount, counts);
    }

    public async Task<ModerationThreadRow?> GetModerationThreadRowAsync(
        Guid conversationId, CancellationToken cancellationToken = default)
    {
        var conversation = await _dbContext.Conversations
            .AsNoTracking()
            .Include(entity => entity.Renter)
            .FirstOrDefaultAsync(
                entity => entity.Id == conversationId && entity.Kind == ConversationKind.Moderation,
                cancellationToken);

        if (conversation is null)
        {
            return null;
        }

        var rows = await BuildModerationThreadRowsAsync(new[] { conversation }, cancellationToken);
        return rows.Single();
    }

    // Shared by ListModerationThreadsAsync and GetModerationThreadRowAsync: resolves the
    // per-conversation unread count, last-message-from-member flag, and the member's
    // listing/rental/open-flag counts in a few batched queries — never one query per row.
    private async Task<List<ModerationThreadRow>> BuildModerationThreadRowsAsync(
        IReadOnlyCollection<Conversation> conversations, CancellationToken cancellationToken)
    {
        if (conversations.Count == 0)
        {
            return new List<ModerationThreadRow>();
        }

        var conversationIds = conversations.Select(conversation => conversation.Id).ToList();
        var memberIds = conversations.Select(conversation => conversation.RenterId).Distinct().ToList();

        // Every participant row for these conversations (two per conversation: the opening
        // moderator + the member) — matched back to each conversation's OwnerId in C# below,
        // since a flat SQL join can't correlate "the participant whose UserId equals THIS ROW's
        // Conversation.OwnerId" without per-row conversation context.
        var participants = await _dbContext.ConversationParticipants
            .AsNoTracking()
            .Where(participant => conversationIds.Contains(participant.ConversationId))
            .ToListAsync(cancellationToken);
        var participantsByConversation = participants
            .GroupBy(participant => participant.ConversationId)
            .ToDictionary(group => group.Key, group => group.ToList());

        var messages = await _dbContext.ChatMessages
            .AsNoTracking()
            .Where(message => conversationIds.Contains(message.ConversationId))
            .Select(message => new ModerationMessageInfo(message.Id, message.ConversationId, message.SenderId, message.Type, message.NoteSubject, message.CreatedAt))
            .ToListAsync(cancellationToken);
        var messagesByConversation = messages
            .GroupBy(message => message.ConversationId)
            .ToDictionary(group => group.Key, group => group.ToList());

        var listingCounts = await _dbContext.Listings
            .Where(listing => memberIds.Contains(listing.OwnerId) && listing.Status == ListingStatus.Approved)
            .GroupBy(listing => listing.OwnerId)
            .Select(group => new { OwnerId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.OwnerId, row => row.Count, cancellationToken);

        var rentalCounts = await _dbContext.Bookings
            .Where(booking => memberIds.Contains(booking.RenterId) && booking.Status == BookingStatus.Completed)
            .GroupBy(booking => booking.RenterId)
            .Select(group => new { RenterId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.RenterId, row => row.Count, cancellationToken);

        var openFlagCounts = await _dbContext.Reports
            .Where(report =>
                report.TargetType == ReportTargetType.User &&
                memberIds.Contains(report.TargetId) &&
                report.Status == ReportStatus.Open)
            .GroupBy(report => report.TargetId)
            .Select(group => new { TargetId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.TargetId, row => row.Count, cancellationToken);

        return conversations.Select(conversation =>
        {
            var conversationMessages = messagesByConversation.TryGetValue(conversation.Id, out var msgs)
                ? msgs
                : new List<ModerationMessageInfo>();

            var ownerLastReadAt = participantsByConversation.TryGetValue(conversation.Id, out var conversationParticipants)
                ? conversationParticipants.FirstOrDefault(participant => participant.UserId == conversation.OwnerId)?.LastReadAt
                : null;

            var unreadCount = conversationMessages.Count(message =>
                message.SenderId == conversation.RenterId &&
                (ownerLastReadAt == null || message.CreatedAt > ownerLastReadAt));

            var lastMessage = conversation.LastMessageId is { } lastMessageId
                ? conversationMessages.FirstOrDefault(message => message.Id == lastMessageId)
                : null;
            var lastMessageFromMember = lastMessage is not null && lastMessage.SenderId == conversation.RenterId;
            var lastMessageNoteSubject = lastMessage is not null && lastMessage.Type == MessageType.ModerationNote
                ? lastMessage.NoteSubject
                : null;

            return new ModerationThreadRow(
                ConversationId: conversation.Id,
                MemberId: conversation.RenterId,
                MemberFirstName: conversation.Renter.FirstName,
                MemberLastName: conversation.Renter.LastName,
                MemberAvatarUrl: conversation.Renter.AvatarUrl,
                MemberIsBlocked: conversation.Renter.IsBlocked,
                MemberIsIdConfirmed: conversation.Renter.IsIdConfirmed,
                MemberListingCount: listingCounts.GetValueOrDefault(conversation.RenterId),
                MemberRentalCount: rentalCounts.GetValueOrDefault(conversation.RenterId),
                MemberOpenFlagCount: openFlagCounts.GetValueOrDefault(conversation.RenterId),
                UnreadCount: unreadCount,
                LastMessageSnippet: conversation.LastMessageSnippet,
                LastMessageAt: conversation.LastMessageAt,
                LastMessageFromMember: lastMessageFromMember,
                LastMessageType: lastMessage?.Type,
                LastMessageNoteSubject: lastMessageNoteSubject,
                CreatedAt: conversation.CreatedAt);
        }).ToList();
    }

    public async Task<bool> CloseForBookingAsync(
        Guid bookingId,
        DateTime closedAtUtc,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var conversation = await _dbContext.Conversations
                .FirstOrDefaultAsync(conversation => conversation.BookingId == bookingId, cancellationToken);

            if (conversation is null || conversation.ClosedAt is not null)
            {
                return false;
            }

            conversation.ClosedAt = closedAtUtc;
            await _dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (Exception exception)
        {
            // Best-effort, mirrors IChatSystemMessageEmitter: never let a failure here break the
            // review submission or booking completion that triggered it.
            _logger.LogError(
                exception,
                "Failed to close chat conversation for booking {BookingId}.",
                bookingId);
            return false;
        }
    }
}
