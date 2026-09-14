using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.Common;
using RentalPlatform.Application.DTOs;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Application.Services;

/// <summary>
/// Admin console Phase 1 (moderation messages): the Messages screen backend. Follows the same
/// shape as AdminUsersService/AdminReportsService — EnsureAdminAsync re-checks the role the
/// controller's [Authorize] already gates (defence in depth), errors are ServiceResult/
/// ServiceError. Everything else (send/read/detail) is the existing /api/chat surface — this
/// service only covers the thread queue and get-or-create.
/// </summary>
public sealed class AdminMessagesService : IAdminMessagesService
{
    private static class ErrorCodes
    {
        public const string Unauthenticated = "admin.unauthenticated";
        public const string Forbidden = "admin.forbidden";
        public const string UserNotFound = "admin.user_not_found";
        public const string CannotMessageSelf = "admin.cannot_message_self";
        public const string CannotMessageAdmin = "admin.cannot_message_admin";
    }

    private const int DefaultPage = 1;
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 100;

    private readonly ICurrentUserContext _currentUserContext;
    private readonly IAdminUsersStore _adminUsersStore;
    private readonly IConversationsStore _conversationsStore;

    public AdminMessagesService(
        ICurrentUserContext currentUserContext,
        IAdminUsersStore adminUsersStore,
        IConversationsStore conversationsStore)
    {
        _currentUserContext = currentUserContext;
        _adminUsersStore = adminUsersStore;
        _conversationsStore = conversationsStore;
    }

    public async Task<ServiceResult<AdminMessageThreadQueueResponse>> GetThreadsAsync(
        AdminMessageThreadFilter filter, CancellationToken cancellationToken = default)
    {
        var adminResult = await EnsureAdminAsync(cancellationToken);
        if (!adminResult.IsSuccess)
        {
            return ServiceResult<AdminMessageThreadQueueResponse>.Failure(adminResult.Error!);
        }

        var threadFilter = ParseFilter(filter.Filter);
        var page = filter.Page < 1 ? DefaultPage : filter.Page;
        var pageSize = filter.PageSize < 1 ? DefaultPageSize : Math.Min(filter.PageSize, MaxPageSize);
        var search = string.IsNullOrWhiteSpace(filter.Search) ? null : filter.Search.Trim();

        var pageResult = await _conversationsStore.ListModerationThreadsAsync(
            threadFilter, search, page, pageSize, cancellationToken);

        var items = pageResult.Items.Select(MapToResponse).ToList();
        var totalPages = pageSize == 0 ? 0 : (int)Math.Ceiling(pageResult.TotalCount / (double)pageSize);

        return ServiceResult<AdminMessageThreadQueueResponse>.Success(new AdminMessageThreadQueueResponse
        {
            Items = items,
            Page = page,
            PageSize = pageSize,
            TotalCount = pageResult.TotalCount,
            TotalPages = totalPages,
            Counts = new AdminMessageThreadCounts
            {
                All = pageResult.Counts.All,
                Unread = pageResult.Counts.Unread,
                NeedsReply = pageResult.Counts.NeedsReply
            }
        });
    }

    public async Task<ServiceResult<AdminMessageThreadResponse>> OpenThreadAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        var adminResult = await EnsureAdminAsync(cancellationToken);
        if (!adminResult.IsSuccess)
        {
            return ServiceResult<AdminMessageThreadResponse>.Failure(adminResult.Error!);
        }

        var admin = adminResult.Value!;

        if (userId == admin.Id)
        {
            return Failure<AdminMessageThreadResponse>(
                ErrorCodes.CannotMessageSelf, "You cannot open a message thread with your own account.");
        }

        var member = await _adminUsersStore.FindUserByIdAsync(userId, cancellationToken);
        if (member is null)
        {
            return Failure<AdminMessageThreadResponse>(ErrorCodes.UserNotFound, "User was not found.");
        }

        if (member.Role == UserRole.Admin)
        {
            return Failure<AdminMessageThreadResponse>(
                ErrorCodes.CannotMessageAdmin, "You cannot open a message thread with another admin.");
        }

        var conversation = await _conversationsStore.GetOrCreateForModerationAsync(admin.Id, userId, cancellationToken);

        var row = await _conversationsStore.GetModerationThreadRowAsync(conversation.Id, cancellationToken);
        // row cannot be null here: the conversation was just resolved (get-or-created) above and
        // nothing in this service deletes conversations.
        return ServiceResult<AdminMessageThreadResponse>.Success(MapToResponse(row!));
    }

    // Returns the authenticated admin User or a failure result. Mirrors
    // AdminUsersService.EnsureAdminAsync / AdminReportsService.EnsureAdminAsync exactly (defence
    // in depth alongside the controller's [Authorize(Roles = "Admin")]).
    private async Task<ServiceResult<User>> EnsureAdminAsync(CancellationToken cancellationToken)
    {
        if (_currentUserContext.UserId is not { } userId)
        {
            return Failure<User>(ErrorCodes.Unauthenticated, "Current user is not authenticated.");
        }

        var user = await _adminUsersStore.FindUserByIdAsync(userId, cancellationToken);
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

    private static AdminMessageThreadResponse MapToResponse(ModerationThreadRow row) => new()
    {
        ConversationId = row.ConversationId,
        MemberId = row.MemberId,
        MemberFirstName = row.MemberFirstName,
        MemberLastName = row.MemberLastName,
        MemberAvatarUrl = row.MemberAvatarUrl,
        MemberStatus = DeriveStatus(row.MemberIsBlocked, row.MemberIsIdConfirmed),
        MemberIsIdConfirmed = row.MemberIsIdConfirmed,
        MemberMarketplaceRole = DeriveMarketplaceRole(row.MemberListingCount, row.MemberRentalCount),
        MemberOpenFlagCount = row.MemberOpenFlagCount,
        UnreadCount = row.UnreadCount,
        LastMessageSnippet = row.LastMessageSnippet,
        LastMessageAt = row.LastMessageAt,
        LastMessageType = row.LastMessageType is { } type ? ChatTokens.MessageTypeToken(type) : null,
        LastMessageNoteSubject = row.LastMessageNoteSubject,
        NeedsReply = row.LastMessageFromMember,
        CreatedAt = row.CreatedAt
    };

    // Same derivation as AdminUsersService.DeriveStatus: Suspended takes priority over Pending —
    // IsBlocked wins regardless of IsIdConfirmed.
    private static UserAccountStatus DeriveStatus(bool isBlocked, bool isIdConfirmed) =>
        isBlocked ? UserAccountStatus.Suspended
            : isIdConfirmed ? UserAccountStatus.Active
            : UserAccountStatus.Pending;

    // Same derivation as AdminUsersService.DeriveMarketplaceRole.
    private static MarketplaceRole DeriveMarketplaceRole(int listingCount, int rentalCount) =>
        (listingCount > 0, rentalCount > 0) switch
        {
            (true, true) => MarketplaceRole.Both,
            (true, false) => MarketplaceRole.Owner,
            (false, true) => MarketplaceRole.Renter,
            (false, false) => MarketplaceRole.Renter
        };

    // "all" | "unread" | "needsReply", case-insensitive; anything else (including null/empty)
    // defaults to "all".
    private static ModerationThreadFilter ParseFilter(string? filter) => filter?.Trim().ToLowerInvariant() switch
    {
        "unread" => ModerationThreadFilter.Unread,
        "needsreply" => ModerationThreadFilter.NeedsReply,
        _ => ModerationThreadFilter.All
    };

    private static ServiceResult<T> Failure<T>(string code, string message) =>
        ServiceResult<T>.Failure(new ServiceError { Code = code, Message = message });
}
