using RentalPlatform.Domain.Entities;

namespace RentalPlatform.Application.Abstractions;

/// <summary>Filter applied to a notification feed query.</summary>
public enum NotificationFeedFilter
{
    All = 0,
    Unread = 1,
    Action = 2
}

/// <summary>A page of notifications plus the cursor to fetch the next page.</summary>
public sealed record NotificationPage(
    IReadOnlyList<Notification> Items,
    string? NextCursor);

/// <summary>Whole-feed counts backing the filter-tab badges.</summary>
public sealed record NotificationCounts(int All, int Unread, int Action);

public interface INotificationsStore
{
    Task<NotificationPage> GetFeedAsync(
        Guid recipientId,
        NotificationFeedFilter filter,
        string? cursor,
        int pageSize,
        CancellationToken cancellationToken = default);

    Task<NotificationCounts> GetCountsAsync(Guid recipientId, CancellationToken cancellationToken = default);

    Task<int> GetUnreadCountAsync(Guid recipientId, CancellationToken cancellationToken = default);

    /// <summary>Marks one notification read. Returns false when it does not exist for this recipient.</summary>
    Task<bool> MarkReadAsync(Guid recipientId, Guid notificationId, DateTime readAt, CancellationToken cancellationToken = default);

    Task MarkAllReadAsync(Guid recipientId, DateTime readAt, CancellationToken cancellationToken = default);

    Task AddAsync(Notification notification, CancellationToken cancellationToken = default);

    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
