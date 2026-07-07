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

        if (existing is not null)
        {
            return existing;
        }

        var primaryImage = booking.Listing.Images
            .OrderByDescending(image => image.IsPrimary)
            .ThenBy(image => image.SortOrder)
            .FirstOrDefault();

        var now = DateTime.UtcNow;
        var conversation = new Conversation
        {
            Id = Guid.NewGuid(),
            BookingId = bookingId,
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

        return conversations
            .Select(conversation =>
            {
                var counterpart = conversation.OwnerId == userId ? conversation.Renter : conversation.Owner;
                var unread = unreadCounts.TryGetValue(conversation.Id, out var count) ? count : 0;
                return new ChatConversationListItem(conversation, counterpart, conversation.Booking, unread);
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
