using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.Common;
using RentalPlatform.Application.DTOs;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Application.Services;

/// <summary>
/// Admin console Phase 3: the Users screen. Follows the same shape as AdminListingsService /
/// AdminCategoriesService — EnsureAdminAsync re-checks the role the controller's [Authorize]
/// already gates (defence in depth), every mutation writes a ModerationLogEntry, errors are
/// ServiceResult/ServiceError. Status/MarketplaceRole are derived, not persisted — no migration.
/// </summary>
public sealed class AdminUsersService : IAdminUsersService
{
    private static class ErrorCodes
    {
        public const string Unauthenticated = "admin.unauthenticated";
        public const string Forbidden = "admin.forbidden";
        public const string UserNotFound = "admin.user_not_found";
        public const string CannotSuspendSelf = "admin.cannot_suspend_self";
        public const string CannotSuspendAdmin = "admin.cannot_suspend_admin";
    }

    private const int DefaultPage = 1;
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 100;
    private const int MaxTargetLabelLength = 300;

    private readonly ICurrentUserContext _currentUserContext;
    private readonly IAdminUsersStore _store;
    private readonly IModerationLogStore _moderationLogStore;

    public AdminUsersService(
        ICurrentUserContext currentUserContext,
        IAdminUsersStore store,
        IModerationLogStore moderationLogStore)
    {
        _currentUserContext = currentUserContext;
        _store = store;
        _moderationLogStore = moderationLogStore;
    }

    public async Task<ServiceResult<AdminUserQueueResponse>> GetQueueAsync(
        AdminUserQueueFilter filter, CancellationToken cancellationToken = default)
    {
        var adminResult = await EnsureAdminAsync(cancellationToken);
        if (!adminResult.IsSuccess)
        {
            return ServiceResult<AdminUserQueueResponse>.Failure(adminResult.Error!);
        }

        var status = ParseStatus(filter.Status);
        var sort = ParseSort(filter.Sort);
        var page = filter.Page < 1 ? DefaultPage : filter.Page;
        var pageSize = filter.PageSize < 1 ? DefaultPageSize : Math.Min(filter.PageSize, MaxPageSize);
        var search = string.IsNullOrWhiteSpace(filter.Search) ? null : filter.Search.Trim();

        var pageResult = await _store.GetUsersPageAsync(status, search, sort, page, pageSize, cancellationToken);
        var counts = await _store.GetStatusCountsAsync(search, cancellationToken);

        var items = pageResult.Items.Select(MapToSummary).ToList();
        var totalPages = pageSize == 0 ? 0 : (int)Math.Ceiling(pageResult.TotalCount / (double)pageSize);

        return ServiceResult<AdminUserQueueResponse>.Success(new AdminUserQueueResponse
        {
            Items = items,
            Page = page,
            PageSize = pageSize,
            TotalCount = pageResult.TotalCount,
            TotalPages = totalPages,
            Summary = new AdminUserQueueSummary
            {
                TotalUsers = counts.Total,
                VerifiedCount = counts.Verified,
                PendingCount = counts.Pending,
                SuspendedCount = counts.Suspended
            }
        });
    }

    public async Task<ServiceResult<AdminUserSummaryResponse>> GetByIdAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        var adminResult = await EnsureAdminAsync(cancellationToken);
        if (!adminResult.IsSuccess)
        {
            return ServiceResult<AdminUserSummaryResponse>.Failure(adminResult.Error!);
        }

        var row = await _store.FindRowByIdAsync(userId, cancellationToken);
        if (row is null)
        {
            return Failure<AdminUserSummaryResponse>(ErrorCodes.UserNotFound, "User was not found.");
        }

        return ServiceResult<AdminUserSummaryResponse>.Success(MapToSummary(row));
    }

    public async Task<ServiceResult<AdminUserSummaryResponse>> VerifyAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        var adminResult = await EnsureAdminAsync(cancellationToken);
        if (!adminResult.IsSuccess)
        {
            return ServiceResult<AdminUserSummaryResponse>.Failure(adminResult.Error!);
        }

        var admin = adminResult.Value!;

        var user = await _store.FindUserByIdAsync(userId, cancellationToken);
        if (user is null)
        {
            return Failure<AdminUserSummaryResponse>(ErrorCodes.UserNotFound, "User was not found.");
        }

        if (!user.IsIdConfirmed)
        {
            user.IsIdConfirmed = true;
            await _store.SaveChangesAsync(cancellationToken);

            await _moderationLogStore.AppendAsync(new ModerationLogEntry
            {
                Id = Guid.NewGuid(),
                ActorUserId = admin.Id,
                Action = ModerationAction.UserVerified,
                TargetType = ModerationTargetType.User,
                TargetId = user.Id,
                TargetLabel = Truncate(FullName(user)),
                DetailJson = null,
                CreatedAt = DateTime.UtcNow
            }, cancellationToken);
        }
        // else: already verified — idempotent no-op, still 200, no log entry (same convention as
        // AdminCategoriesService.UpdateVisibilityAsync's already-at-target case).

        return await BuildResponseAsync(user.Id, cancellationToken);
    }

    public async Task<ServiceResult<AdminUserSummaryResponse>> SuspendAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        var adminResult = await EnsureAdminAsync(cancellationToken);
        if (!adminResult.IsSuccess)
        {
            return ServiceResult<AdminUserSummaryResponse>.Failure(adminResult.Error!);
        }

        var admin = adminResult.Value!;

        // Compared against the authenticated admin's own id from ICurrentUserContext — never a
        // client-supplied value — so this can't be spoofed by a body/query parameter.
        if (userId == admin.Id)
        {
            return Failure<AdminUserSummaryResponse>(
                ErrorCodes.CannotSuspendSelf, "You cannot suspend your own account.");
        }

        var user = await _store.FindUserByIdAsync(userId, cancellationToken);
        if (user is null)
        {
            return Failure<AdminUserSummaryResponse>(ErrorCodes.UserNotFound, "User was not found.");
        }

        if (user.Role == UserRole.Admin)
        {
            return Failure<AdminUserSummaryResponse>(
                ErrorCodes.CannotSuspendAdmin, "Admins cannot suspend another admin.");
        }

        if (!user.IsBlocked)
        {
            user.IsBlocked = true;
            await _store.SaveChangesAsync(cancellationToken);

            await _moderationLogStore.AppendAsync(new ModerationLogEntry
            {
                Id = Guid.NewGuid(),
                ActorUserId = admin.Id,
                Action = ModerationAction.UserSuspended,
                TargetType = ModerationTargetType.User,
                TargetId = user.Id,
                TargetLabel = Truncate(FullName(user)),
                DetailJson = null,
                CreatedAt = DateTime.UtcNow
            }, cancellationToken);
        }
        // else: already suspended — idempotent no-op, same convention as VerifyAsync above.

        return await BuildResponseAsync(user.Id, cancellationToken);
    }

    public async Task<ServiceResult<AdminUserSummaryResponse>> ReactivateAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        var adminResult = await EnsureAdminAsync(cancellationToken);
        if (!adminResult.IsSuccess)
        {
            return ServiceResult<AdminUserSummaryResponse>.Failure(adminResult.Error!);
        }

        var admin = adminResult.Value!;

        var user = await _store.FindUserByIdAsync(userId, cancellationToken);
        if (user is null)
        {
            return Failure<AdminUserSummaryResponse>(ErrorCodes.UserNotFound, "User was not found.");
        }

        if (user.IsBlocked)
        {
            user.IsBlocked = false;
            await _store.SaveChangesAsync(cancellationToken);

            await _moderationLogStore.AppendAsync(new ModerationLogEntry
            {
                Id = Guid.NewGuid(),
                ActorUserId = admin.Id,
                Action = ModerationAction.UserReactivated,
                TargetType = ModerationTargetType.User,
                TargetId = user.Id,
                TargetLabel = Truncate(FullName(user)),
                DetailJson = null,
                CreatedAt = DateTime.UtcNow
            }, cancellationToken);
        }
        // else: already active — idempotent no-op, same convention as VerifyAsync above.

        return await BuildResponseAsync(user.Id, cancellationToken);
    }

    private async Task<ServiceResult<AdminUserSummaryResponse>> BuildResponseAsync(
        Guid userId, CancellationToken cancellationToken)
    {
        var row = await _store.FindRowByIdAsync(userId, cancellationToken);
        // row cannot be null here: the caller just found/mutated this exact user id in the same
        // request, and nothing in this service deletes users.
        return ServiceResult<AdminUserSummaryResponse>.Success(MapToSummary(row!));
    }

    // Returns the authenticated admin User or a failure result. Mirrors
    // AdminListingsService.EnsureAdminAsync / AdminCategoriesService.EnsureAdminAsync exactly
    // (defence in depth alongside the controller's [Authorize(Roles = "Admin")]).
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

    private static AdminUserSummaryResponse MapToSummary(AdminUserRow row) => new()
    {
        Id = row.Id,
        Email = row.Email,
        FirstName = row.FirstName,
        LastName = row.LastName,
        AvatarUrl = row.AvatarUrl,
        Role = row.Role,
        Status = DeriveStatus(row.IsBlocked, row.IsIdConfirmed),
        IsIdConfirmed = row.IsIdConfirmed,
        MarketplaceRole = DeriveMarketplaceRole(row.ListingCount, row.RentalCount),
        ListingCount = row.ListingCount,
        RentalCount = row.RentalCount,
        FlagCount = row.FlagCount,
        CreatedAt = row.CreatedAt
    };

    // Suspended takes priority over Pending: IsBlocked wins regardless of IsIdConfirmed.
    private static UserAccountStatus DeriveStatus(bool isBlocked, bool isIdConfirmed) =>
        isBlocked ? UserAccountStatus.Suspended
            : isIdConfirmed ? UserAccountStatus.Active
            : UserAccountStatus.Pending;

    private static MarketplaceRole DeriveMarketplaceRole(int listingCount, int rentalCount) =>
        (listingCount > 0, rentalCount > 0) switch
        {
            (true, true) => MarketplaceRole.Both,
            (true, false) => MarketplaceRole.Owner,
            (false, true) => MarketplaceRole.Renter,
            (false, false) => MarketplaceRole.Renter
        };

    // "all" | "pending" | "suspended", case-insensitive; anything else (including null/empty, and
    // "active" — not a filter value the UI exposes) defaults to no status filter ("all").
    private static UserAccountStatus? ParseStatus(string? status) => status?.Trim().ToLowerInvariant() switch
    {
        "pending" => UserAccountStatus.Pending,
        "suspended" => UserAccountStatus.Suspended,
        _ => null
    };

    // "name" | "newest" | "listings" | "rentals" | "flags", case-insensitive; anything else
    // (including null/empty) defaults to "name".
    private static AdminUserSort ParseSort(string? sort) => sort?.Trim().ToLowerInvariant() switch
    {
        "newest" => AdminUserSort.Newest,
        "listings" => AdminUserSort.Listings,
        "rentals" => AdminUserSort.Rentals,
        "flags" => AdminUserSort.Flags,
        _ => AdminUserSort.Name
    };

    private static string FullName(User user) => $"{user.FirstName} {user.LastName}".Trim();

    private static string Truncate(string value) =>
        value.Length <= MaxTargetLabelLength ? value : value[..MaxTargetLabelLength];

    private static ServiceResult<T> Failure<T>(string code, string message) =>
        ServiceResult<T>.Failure(new ServiceError { Code = code, Message = message });
}
