using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.Common;
using RentalPlatform.Application.DTOs;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Application.Services;

/// <summary>
/// Admin console Phase 5: the Overview screen. Follows the same shape as
/// AdminListingsService/AdminReportsService — EnsureAdminAsync re-checks the role the controller's
/// [Authorize] already gates (defence in depth) — but this screen is read-only, so there is no
/// ModerationLogEntry write path here.
/// </summary>
public sealed class AdminOverviewService : IAdminOverviewService
{
    private static class ErrorCodes
    {
        public const string Unauthenticated = "admin.unauthenticated";
        public const string Forbidden = "admin.forbidden";
    }

    // The "Queue health · target < 6 h" line on the design. One named constant, per the task spec,
    // so it's the one obvious place to change if the target ever moves.
    private const int ReviewTimeTargetHours = 6;

    private const int DefaultActivityTake = 20;
    private const int MaxActivityTake = 50;

    private readonly ICurrentUserContext _currentUserContext;
    private readonly IAdminOverviewStore _store;

    public AdminOverviewService(ICurrentUserContext currentUserContext, IAdminOverviewStore store)
    {
        _currentUserContext = currentUserContext;
        _store = store;
    }

    public async Task<ServiceResult<AdminOverviewResponse>> GetOverviewAsync(
        CancellationToken cancellationToken = default)
    {
        var adminResult = await EnsureAdminAsync(cancellationToken);
        if (!adminResult.IsSuccess)
        {
            return ServiceResult<AdminOverviewResponse>.Failure(adminResult.Error!);
        }

        var stats = await _store.GetStatsAsync(DateTime.UtcNow, cancellationToken);

        return ServiceResult<AdminOverviewResponse>.Success(new AdminOverviewResponse
        {
            AwaitingReviewCount = stats.AwaitingReviewCount,
            OldestAwaitingCreatedAt = stats.OldestAwaitingCreatedAt,
            LiveListingCount = stats.LiveListingCount,
            LiveListingsAddedThisWeek = stats.LiveListingsAddedThisWeek,
            CategoryCount = stats.CategoryCount,
            VisibleCategoryCount = stats.VisibleCategoryCount,
            OpenReportCount = stats.OpenReportCount,
            AverageReviewTimeMinutes = stats.AverageReviewTimeMinutes,
            ReviewTimeTargetHours = ReviewTimeTargetHours
        });
    }

    public async Task<ServiceResult<AdminActivityFeedResponse>> GetActivityFeedAsync(
        int take, CancellationToken cancellationToken = default)
    {
        var adminResult = await EnsureAdminAsync(cancellationToken);
        if (!adminResult.IsSuccess)
        {
            return ServiceResult<AdminActivityFeedResponse>.Failure(adminResult.Error!);
        }

        var clampedTake = take < 1 ? DefaultActivityTake : Math.Min(take, MaxActivityTake);

        var entries = await _store.GetRecentActivityAsync(clampedTake, cancellationToken);

        var actorIds = entries.Select(entry => entry.ActorUserId).Distinct().ToList();
        var actors = await _store.GetUsersByIdsAsync(actorIds, cancellationToken);

        var items = entries.Select(entry =>
        {
            actors.TryGetValue(entry.ActorUserId, out var actor);
            return new AdminActivityItemResponse
            {
                Id = entry.Id,
                Action = entry.Action,
                TargetType = entry.TargetType,
                TargetId = entry.TargetId,
                TargetLabel = entry.TargetLabel,
                CreatedAt = entry.CreatedAt,
                ActorId = entry.ActorUserId,
                // Null when the actor row no longer exists — the row itself is still returned
                // (see IAdminOverviewStore.GetUsersByIdsAsync doc): the audit trail must not
                // develop holes just because an actor account was later deleted.
                ActorFirstName = actor?.FirstName,
                ActorLastName = actor?.LastName,
                ActorAvatarUrl = actor?.AvatarUrl,
                DetailJson = entry.DetailJson
            };
        }).ToList();

        return ServiceResult<AdminActivityFeedResponse>.Success(new AdminActivityFeedResponse { Items = items });
    }

    // Returns the authenticated admin User or a failure result. Mirrors
    // AdminListingsService.EnsureAdminAsync / AdminReportsService.EnsureAdminAsync exactly.
    private async Task<ServiceResult<User>> EnsureAdminAsync(CancellationToken cancellationToken)
    {
        if (_currentUserContext.UserId is not { } userId)
        {
            return Failure<User>(ErrorCodes.Unauthenticated, "Current user is not authenticated.");
        }

        var user = await _store.FindUserByIdAsync(userId, cancellationToken);
        if (user is null)
        {
            return Failure<User>(ErrorCodes.Unauthenticated, "Current user is not authenticated.");
        }

        if (user.Role != UserRole.Admin || user.IsBlocked)
        {
            return Failure<User>(ErrorCodes.Forbidden, "Admin privileges are required.");
        }

        return ServiceResult<User>.Success(user);
    }

    private static ServiceResult<T> Failure<T>(string code, string message) =>
        ServiceResult<T>.Failure(new ServiceError { Code = code, Message = message });
}
