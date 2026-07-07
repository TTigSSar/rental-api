using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.Common;
using RentalPlatform.Application.DTOs;
using RentalPlatform.Domain.Entities;

namespace RentalPlatform.Application.Services;

public sealed class NotificationsService : INotificationsService
{
    private const int PageSize = 20;

    private static class ErrorCodes
    {
        public const string Unauthenticated = "notification.unauthenticated";
        public const string InvalidFilter = "notification.invalid_filter";
        public const string NotFound = "notification.not_found";
    }

    private readonly ICurrentUserContext _currentUserContext;
    private readonly INotificationsStore _store;

    public NotificationsService(ICurrentUserContext currentUserContext, INotificationsStore store)
    {
        _currentUserContext = currentUserContext;
        _store = store;
    }

    public async Task<ServiceResult<NotificationFeedResponse>> GetFeedAsync(
        string? filter,
        string? cursor,
        CancellationToken cancellationToken = default)
    {
        if (_currentUserContext.UserId is not { } userId)
        {
            return Failure<NotificationFeedResponse>(ErrorCodes.Unauthenticated, "Current user is not authenticated.");
        }

        if (!NotificationTokens.TryParseFilter(filter, out var feedFilter))
        {
            return Failure<NotificationFeedResponse>(ErrorCodes.InvalidFilter, "Unknown notification filter.");
        }

        var page = await _store.GetFeedAsync(userId, feedFilter, cursor, PageSize, cancellationToken);
        var counts = await _store.GetCountsAsync(userId, cancellationToken);

        var response = new NotificationFeedResponse
        {
            Items = page.Items.Select(MapNotification).ToList(),
            NextCursor = page.NextCursor,
            Counts = new NotificationCountsResponse
            {
                All = counts.All,
                Unread = counts.Unread,
                Action = counts.Action
            }
        };

        return ServiceResult<NotificationFeedResponse>.Success(response);
    }

    public async Task<ServiceResult<NotificationUnreadCountResponse>> GetUnreadCountAsync(
        CancellationToken cancellationToken = default)
    {
        if (_currentUserContext.UserId is not { } userId)
        {
            return Failure<NotificationUnreadCountResponse>(ErrorCodes.Unauthenticated, "Current user is not authenticated.");
        }

        var unread = await _store.GetUnreadCountAsync(userId, cancellationToken);
        return ServiceResult<NotificationUnreadCountResponse>.Success(
            new NotificationUnreadCountResponse { UnreadCount = unread });
    }

    public async Task<ServiceResult<bool>> MarkReadAsync(Guid notificationId, CancellationToken cancellationToken = default)
    {
        if (_currentUserContext.UserId is not { } userId)
        {
            return Failure<bool>(ErrorCodes.Unauthenticated, "Current user is not authenticated.");
        }

        var updated = await _store.MarkReadAsync(userId, notificationId, DateTime.UtcNow, cancellationToken);
        if (!updated)
        {
            return Failure<bool>(ErrorCodes.NotFound, "Notification was not found.");
        }

        return ServiceResult<bool>.Success(true);
    }

    public async Task<ServiceResult<bool>> MarkAllReadAsync(CancellationToken cancellationToken = default)
    {
        if (_currentUserContext.UserId is not { } userId)
        {
            return Failure<bool>(ErrorCodes.Unauthenticated, "Current user is not authenticated.");
        }

        await _store.MarkAllReadAsync(userId, DateTime.UtcNow, cancellationToken);
        return ServiceResult<bool>.Success(true);
    }

    private static NotificationResponse MapNotification(Notification notification) => new()
    {
        Id = notification.Id,
        Kind = NotificationTokens.KindToken(notification.Kind),
        Category = NotificationTokens.CategoryToken(notification.Category),
        Title = notification.Title,
        Body = notification.Body,
        Meta = notification.Meta,
        CreatedAt = notification.CreatedAt,
        Read = notification.ReadAt is not null,
        Urgent = notification.Urgent,
        Actor = new NotificationActorResponse
        {
            Name = notification.ActorName,
            AvatarUrl = notification.ActorAvatarUrl,
            Verified = notification.ActorVerified,
            System = notification.ActorIsSystem,
            SystemIcon = notification.ActorSystemIcon
        },
        Toy = notification.ToyTitle is null
            ? null
            : new NotificationToyResponse
            {
                ImageUrl = notification.ToyImageUrl,
                Title = notification.ToyTitle
            },
        PrimaryAction = notification.PrimaryActionLabel is null || notification.PrimaryActionDeepLink is null
            ? null
            : new NotificationActionResponse
            {
                Label = notification.PrimaryActionLabel,
                DeepLink = notification.PrimaryActionDeepLink
            },
        SecondaryAction = notification.SecondaryActionLabel is null || notification.SecondaryActionDeepLink is null
            ? null
            : new NotificationActionResponse
            {
                Label = notification.SecondaryActionLabel,
                DeepLink = notification.SecondaryActionDeepLink
            }
    };

    private static ServiceResult<T> Failure<T>(string code, string message) =>
        ServiceResult<T>.Failure(new ServiceError { Code = code, Message = message });
}
