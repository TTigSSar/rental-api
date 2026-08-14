using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.Common;
using RentalPlatform.Application.DTOs;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Application.Services;

/// <summary>
/// Admin console Phase 4: user-facing report submission. Reports do NOT write a
/// ModerationLogEntry — that log is for moderator actions (see AdminReportsService for the
/// resolve/dismiss/reopen triage actions, which do).
/// </summary>
public sealed class ReportsService : IReportsService
{
    private static class ErrorCodes
    {
        public const string Unauthenticated = "report.unauthenticated";
        public const string UserBlocked = "report.user_blocked";
        public const string InvalidReason = "report.invalid_reason";
        public const string InvalidTargetType = "report.invalid_target_type";
        public const string TargetNotFound = "report.target_not_found";
        public const string CannotReportOwn = "report.cannot_report_own";
        public const string AlreadyReported = "report.already_reported";
    }

    private const int MaxTargetLabelLength = 300;

    private readonly ICurrentUserContext _currentUserContext;
    private readonly IReportsStore _store;

    public ReportsService(ICurrentUserContext currentUserContext, IReportsStore store)
    {
        _currentUserContext = currentUserContext;
        _store = store;
    }

    public async Task<ServiceResult<ReportResponse>> CreateAsync(
        CreateReportRequest request, CancellationToken cancellationToken = default)
    {
        var userResult = await GetCurrentUserAsync(cancellationToken);
        if (!userResult.IsSuccess || userResult.Value is null)
        {
            return ServiceResult<ReportResponse>.Failure(userResult.Error!);
        }

        var reporter = userResult.Value;

        var reasonCode = request.ReasonCode.Trim();
        if (!ReportReasonCatalog.IsKnownCode(reasonCode))
        {
            return Failure<ReportResponse>(ErrorCodes.InvalidReason, "Report reason is invalid.");
        }

        var targetType = ParseTargetType(request.TargetType);
        if (targetType is null)
        {
            return Failure<ReportResponse>(ErrorCodes.InvalidTargetType, "Report target type is invalid.");
        }

        var targetLabelResult = await ResolveTargetAsync(targetType.Value, request.TargetId, reporter, cancellationToken);
        if (!targetLabelResult.IsSuccess)
        {
            return ServiceResult<ReportResponse>.Failure(targetLabelResult.Error!);
        }

        var alreadyReported = await _store.HasOpenReportAsync(
            reporter.Id, targetType.Value, request.TargetId, cancellationToken);
        if (alreadyReported)
        {
            return Failure<ReportResponse>(
                ErrorCodes.AlreadyReported, "You already have an open report against this target.");
        }

        var severity = ReportReasonCatalog.SeverityFor(reasonCode);
        var trimmedDetail = string.IsNullOrWhiteSpace(request.Detail) ? null : request.Detail.Trim();
        var now = DateTime.UtcNow;

        var report = new Report
        {
            Id = Guid.NewGuid(),
            TargetType = targetType.Value,
            TargetId = request.TargetId,
            TargetLabel = Truncate(targetLabelResult.Value!),
            ReporterUserId = reporter.Id,
            ReasonCode = reasonCode,
            Detail = trimmedDetail,
            Severity = severity,
            Status = ReportStatus.Open,
            CreatedAt = now
        };

        await _store.AddAsync(report, cancellationToken);
        await _store.SaveChangesAsync(cancellationToken);

        return ServiceResult<ReportResponse>.Success(new ReportResponse
        {
            Id = report.Id,
            TargetType = report.TargetType,
            TargetId = report.TargetId,
            TargetLabel = report.TargetLabel,
            ReasonCode = report.ReasonCode,
            Detail = report.Detail,
            Severity = report.Severity,
            Status = report.Status,
            CreatedAt = report.CreatedAt
        });
    }

    // Resolves the target's existence + human-readable label, and enforces the self-report guard
    // (a user cannot report themselves or their own listing). Returns the label on success.
    private async Task<ServiceResult<string>> ResolveTargetAsync(
        ReportTargetType targetType, Guid targetId, User reporter, CancellationToken cancellationToken)
    {
        switch (targetType)
        {
            case ReportTargetType.Listing:
                var listing = await _store.FindListingByIdAsync(targetId, cancellationToken);
                if (listing is null)
                {
                    return Failure<string>(ErrorCodes.TargetNotFound, "Report target was not found.");
                }

                if (listing.OwnerId == reporter.Id)
                {
                    return Failure<string>(ErrorCodes.CannotReportOwn, "You cannot report your own listing.");
                }

                return ServiceResult<string>.Success(listing.Title);

            case ReportTargetType.User:
                if (targetId == reporter.Id)
                {
                    return Failure<string>(ErrorCodes.CannotReportOwn, "You cannot report yourself.");
                }

                var user = await _store.FindUserByIdAsync(targetId, cancellationToken);
                if (user is null)
                {
                    return Failure<string>(ErrorCodes.TargetNotFound, "Report target was not found.");
                }

                return ServiceResult<string>.Success(FullName(user));

            case ReportTargetType.Message:
                var conversation = await _store.FindConversationByIdAsync(targetId, cancellationToken);
                if (conversation is null)
                {
                    return Failure<string>(ErrorCodes.TargetNotFound, "Report target was not found.");
                }

                return ServiceResult<string>.Success(ResolveConversationLabel(conversation, reporter.Id));

            default:
                return Failure<string>(ErrorCodes.InvalidTargetType, "Report target type is invalid.");
        }
    }

    // "Chat with <counterpart>" from the reporter's own side of the thread; falls back to the toy
    // title if the reporter is (unexpectedly) not one of the two participants.
    private static string ResolveConversationLabel(Conversation conversation, Guid reporterId)
    {
        if (conversation.OwnerId == reporterId)
        {
            return $"Chat with {FullName(conversation.Renter)}";
        }

        if (conversation.RenterId == reporterId)
        {
            return $"Chat with {FullName(conversation.Owner)}";
        }

        return $"Chat about {conversation.ToyTitle}";
    }

    private async Task<ServiceResult<User>> GetCurrentUserAsync(CancellationToken cancellationToken)
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

        if (user.IsBlocked)
        {
            return Failure<User>(ErrorCodes.UserBlocked, "Blocked users cannot submit reports.");
        }

        return ServiceResult<User>.Success(user);
    }

    // "listing" | "user" | "message", case-insensitive; anything else (including null/empty) is
    // invalid — unlike AdminListingQueueFilter.Status, there is no sensible default for a create
    // request's own target type.
    private static ReportTargetType? ParseTargetType(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "listing" => ReportTargetType.Listing,
        "user" => ReportTargetType.User,
        "message" => ReportTargetType.Message,
        _ => null
    };

    private static string FullName(User user) => $"{user.FirstName} {user.LastName}".Trim();

    private static string Truncate(string value) =>
        value.Length <= MaxTargetLabelLength ? value : value[..MaxTargetLabelLength];

    private static ServiceResult<T> Failure<T>(string code, string message) =>
        ServiceResult<T>.Failure(new ServiceError { Code = code, Message = message });
}
