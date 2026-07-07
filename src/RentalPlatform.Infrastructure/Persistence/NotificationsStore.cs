using System.Text;
using Microsoft.EntityFrameworkCore;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Domain.Entities;

namespace RentalPlatform.Infrastructure.Persistence;

public sealed class NotificationsStore : INotificationsStore
{
    private readonly AppDbContext _dbContext;

    public NotificationsStore(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<NotificationPage> GetFeedAsync(
        Guid recipientId,
        NotificationFeedFilter filter,
        string? cursor,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var query = _dbContext.Notifications
            .AsNoTracking()
            .Where(notification => notification.RecipientId == recipientId);

        query = filter switch
        {
            NotificationFeedFilter.Unread => query.Where(notification => notification.ReadAt == null),
            NotificationFeedFilter.Action => query.Where(notification => notification.Urgent),
            _ => query
        };

        if (TryDecodeCursor(cursor, out var createdBefore))
        {
            query = query.Where(notification => notification.CreatedAt < createdBefore);
        }

        // Fetch one extra row to know whether a further page exists.
        var rows = await query
            .OrderByDescending(notification => notification.CreatedAt)
            .ThenByDescending(notification => notification.Id)
            .Take(pageSize + 1)
            .ToListAsync(cancellationToken);

        var hasMore = rows.Count > pageSize;
        var items = hasMore ? rows.Take(pageSize).ToList() : rows;
        var nextCursor = hasMore ? EncodeCursor(items[^1].CreatedAt) : null;

        return new NotificationPage(items, nextCursor);
    }

    public async Task<NotificationCounts> GetCountsAsync(Guid recipientId, CancellationToken cancellationToken = default)
    {
        var mine = _dbContext.Notifications
            .AsNoTracking()
            .Where(notification => notification.RecipientId == recipientId);

        var all = await mine.CountAsync(cancellationToken);
        var unread = await mine.CountAsync(notification => notification.ReadAt == null, cancellationToken);
        var action = await mine.CountAsync(notification => notification.Urgent, cancellationToken);

        return new NotificationCounts(all, unread, action);
    }

    public Task<int> GetUnreadCountAsync(Guid recipientId, CancellationToken cancellationToken = default) =>
        _dbContext.Notifications
            .AsNoTracking()
            .CountAsync(
                notification => notification.RecipientId == recipientId && notification.ReadAt == null,
                cancellationToken);

    public async Task<bool> MarkReadAsync(
        Guid recipientId,
        Guid notificationId,
        DateTime readAt,
        CancellationToken cancellationToken = default)
    {
        var notification = await _dbContext.Notifications.FirstOrDefaultAsync(
            entity => entity.Id == notificationId && entity.RecipientId == recipientId,
            cancellationToken);

        if (notification is null)
        {
            return false;
        }

        if (notification.ReadAt is null)
        {
            notification.ReadAt = readAt;
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        return true;
    }

    public Task MarkAllReadAsync(Guid recipientId, DateTime readAt, CancellationToken cancellationToken = default) =>
        _dbContext.Notifications
            .Where(notification => notification.RecipientId == recipientId && notification.ReadAt == null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(notification => notification.ReadAt, readAt),
                cancellationToken);

    public async Task AddAsync(Notification notification, CancellationToken cancellationToken = default) =>
        await _dbContext.Notifications.AddAsync(notification, cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
        _dbContext.SaveChangesAsync(cancellationToken);

    // ── Opaque cursor = base64 of the last item's CreatedAt ticks ─────────────
    private static string EncodeCursor(DateTime createdAt) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(createdAt.Ticks.ToString()));

    private static bool TryDecodeCursor(string? cursor, out DateTime createdBefore)
    {
        createdBefore = default;
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return false;
        }

        try
        {
            var ticks = long.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(cursor)));
            createdBefore = new DateTime(ticks, DateTimeKind.Utc);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
