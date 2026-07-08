using Microsoft.EntityFrameworkCore;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Infrastructure.Persistence;

public sealed class ConversationsStore : IConversationsStore
{
    private const int SnippetMaxLength = 500;

    private readonly AppDbContext _dbContext;

    public ConversationsStore(AppDbContext dbContext)
    {
        _dbContext = dbContext;
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

        // Resolve the last message's sender query-time (Conversation only denormalises the
        // snippet/timestamp, not the sender) so the inbox can flag "You: ..." previews without
        // adding a denormalised column.
        var lastMessageIds = conversations
            .Where(conversation => conversation.LastMessageId != null)
            .Select(conversation => conversation.LastMessageId!.Value)
            .ToList();

        var lastMessageSenders = lastMessageIds.Count == 0
            ? new Dictionary<Guid, Guid?>()
            : await _dbContext.ChatMessages
                .AsNoTracking()
                .Where(message => lastMessageIds.Contains(message.Id))
                .ToDictionaryAsync(message => message.Id, message => message.SenderId, cancellationToken);

        return conversations
            .Select(conversation =>
            {
                var counterpart = conversation.OwnerId == userId ? conversation.Renter : conversation.Owner;
                var unread = unreadCounts.TryGetValue(conversation.Id, out var count) ? count : 0;
                var lastMessageSenderId = conversation.LastMessageId is { } lastMessageId
                    && lastMessageSenders.TryGetValue(lastMessageId, out var senderId)
                        ? senderId
                        : null;
                return new ChatConversationListItem(conversation, counterpart, conversation.Booking, unread, lastMessageSenderId);
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
}
