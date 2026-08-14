using System.Text.Encodings.Web;
using System.Text.Json;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.Common;
using RentalPlatform.Application.DTOs;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Application.Services;

/// <summary>
/// Admin console Phase 4: the Reports & flags screen. Follows the same shape as
/// AdminListingsService/AdminUsersService — EnsureAdminAsync re-checks the role the controller's
/// [Authorize] already gates, every triage mutation (resolve/dismiss/reopen) writes a
/// ModerationLogEntry, errors are ServiceResult/ServiceError, and each mutation is a 200 no-op
/// (no log entry) when the report is already at the target status.
/// </summary>
public sealed class AdminReportsService : IAdminReportsService
{
    private static class ErrorCodes
    {
        public const string Unauthenticated = "admin.unauthenticated";
        public const string Forbidden = "admin.forbidden";
        public const string ReportNotFound = "admin.report_not_found";
        public const string InvalidTargetFilter = "admin.report_invalid_target_filter";
        public const string TargetFilterIncomplete = "admin.report_target_filter_incomplete";
    }

    private const int DefaultPage = 1;
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 100;
    private const int MaxTargetLabelLength = 300;

    // ModerationLogEntry.DetailJson is HasMaxLength(2000). The default JavaScriptEncoder escapes
    // every non-ASCII character (Armenian/Russian notes included) as \uXXXX — 6 chars per source
    // character — so an unbounded note can blow the column even though it fits ReportActionRequest's
    // own 1000-char limit. 300 chars of note, worst case fully escaped, is 300*6 + 11 (the
    // {"note":"..."} skeleton) = 1811 <= 2000, with headroom to spare. Truncating the note itself
    // (not the serialised JSON) keeps the emitted string valid JSON.
    private const int MaxDetailNoteLength = 300;

    // UnsafeRelaxedJsonEscaping doesn't escape Armenian/Cyrillic/etc (only HTML-sensitive
    // characters), which shrinks the common case a lot — but MaxDetailNoteLength above is the
    // actual guarantee, not this encoder choice.
    private static readonly JsonSerializerOptions DetailJsonOptions =
        new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly ICurrentUserContext _currentUserContext;
    private readonly IReportsStore _store;
    private readonly IModerationLogStore _moderationLogStore;

    public AdminReportsService(
        ICurrentUserContext currentUserContext,
        IReportsStore store,
        IModerationLogStore moderationLogStore)
    {
        _currentUserContext = currentUserContext;
        _store = store;
        _moderationLogStore = moderationLogStore;
    }

    public async Task<ServiceResult<AdminReportQueueResponse>> GetQueueAsync(
        AdminReportQueueFilter filter, CancellationToken cancellationToken = default)
    {
        var adminResult = await EnsureAdminAsync(cancellationToken);
        if (!adminResult.IsSuccess)
        {
            return ServiceResult<AdminReportQueueResponse>.Failure(adminResult.Error!);
        }

        ReportTargetType? targetType = null;
        if (!string.IsNullOrWhiteSpace(filter.TargetType))
        {
            targetType = ParseTargetType(filter.TargetType);
            if (targetType is null)
            {
                return Failure<AdminReportQueueResponse>(
                    ErrorCodes.InvalidTargetFilter, "Report target type filter is invalid.");
            }
        }

        // A targetId without a targetType is ambiguous — silently ignoring it would show the
        // admin an unfiltered queue that looks filtered, so it's rejected rather than dropped.
        if (targetType is null && filter.TargetId is not null)
        {
            return Failure<AdminReportQueueResponse>(
                ErrorCodes.TargetFilterIncomplete, "A target id filter requires a target type.");
        }

        var status = ParseStatus(filter.Status);
        var page = filter.Page < 1 ? DefaultPage : filter.Page;
        var pageSize = filter.PageSize < 1 ? DefaultPageSize : Math.Min(filter.PageSize, MaxPageSize);
        var search = string.IsNullOrWhiteSpace(filter.Search) ? null : filter.Search.Trim();

        var pageResult = await _store.GetReportsPageAsync(
            status, targetType, filter.TargetId, search, page, pageSize, cancellationToken);
        var counts = await _store.GetStatusCountsAsync(targetType, filter.TargetId, search, cancellationToken);

        var items = await MapRowsAsync(pageResult.Items, cancellationToken);
        var totalPages = pageSize == 0 ? 0 : (int)Math.Ceiling(pageResult.TotalCount / (double)pageSize);

        return ServiceResult<AdminReportQueueResponse>.Success(new AdminReportQueueResponse
        {
            Items = items,
            Page = page,
            PageSize = pageSize,
            TotalCount = pageResult.TotalCount,
            TotalPages = totalPages,
            Counts = new AdminReportCounts
            {
                Open = counts.Open,
                Resolved = counts.Resolved,
                Dismissed = counts.Dismissed,
                All = counts.All
            }
        });
    }

    public async Task<ServiceResult<AdminReportRowResponse>> GetByIdAsync(
        Guid reportId, CancellationToken cancellationToken = default)
    {
        var adminResult = await EnsureAdminAsync(cancellationToken);
        if (!adminResult.IsSuccess)
        {
            return ServiceResult<AdminReportRowResponse>.Failure(adminResult.Error!);
        }

        var report = await _store.FindByIdAsync(reportId, cancellationToken);
        if (report is null)
        {
            return Failure<AdminReportRowResponse>(ErrorCodes.ReportNotFound, "Report was not found.");
        }

        var row = (await MapRowsAsync(new[] { report }, cancellationToken)).Single();
        return ServiceResult<AdminReportRowResponse>.Success(row);
    }

    public Task<ServiceResult<AdminReportRowResponse>> ResolveAsync(
        Guid reportId, string? note, CancellationToken cancellationToken = default) =>
        ApplyStampedActionAsync(reportId, note, ReportStatus.Resolved, ModerationAction.ReportResolved, cancellationToken);

    public Task<ServiceResult<AdminReportRowResponse>> DismissAsync(
        Guid reportId, string? note, CancellationToken cancellationToken = default) =>
        ApplyStampedActionAsync(reportId, note, ReportStatus.Dismissed, ModerationAction.ReportDismissed, cancellationToken);

    public async Task<ServiceResult<AdminReportRowResponse>> ReopenAsync(
        Guid reportId, CancellationToken cancellationToken = default)
    {
        var adminResult = await EnsureAdminAsync(cancellationToken);
        if (!adminResult.IsSuccess)
        {
            return ServiceResult<AdminReportRowResponse>.Failure(adminResult.Error!);
        }

        var admin = adminResult.Value!;

        var report = await _store.FindByIdAsync(reportId, cancellationToken);
        if (report is null)
        {
            return Failure<AdminReportRowResponse>(ErrorCodes.ReportNotFound, "Report was not found.");
        }

        if (report.Status != ReportStatus.Open)
        {
            report.Status = ReportStatus.Open;
            report.ResolvedAt = null;
            report.ResolvedByUserId = null;
            report.ResolutionNote = null;

            await _store.SaveChangesAsync(cancellationToken);

            await _moderationLogStore.AppendAsync(new ModerationLogEntry
            {
                Id = Guid.NewGuid(),
                ActorUserId = admin.Id,
                Action = ModerationAction.ReportReopened,
                TargetType = ModerationTargetType.Report,
                TargetId = report.Id,
                TargetLabel = Truncate(report.TargetLabel),
                DetailJson = null,
                CreatedAt = DateTime.UtcNow
            }, cancellationToken);
        }
        // else: already open — idempotent no-op, same convention as AdminUsersService.VerifyAsync.

        var row = (await MapRowsAsync(new[] { report }, cancellationToken)).Single();
        return ServiceResult<AdminReportRowResponse>.Success(row);
    }

    // Shared by Resolve/Dismiss: both set Status, stamp ResolvedAt/ResolvedByUserId/ResolutionNote,
    // and are 200 no-ops (no log entry) when the report is already at the target status.
    private async Task<ServiceResult<AdminReportRowResponse>> ApplyStampedActionAsync(
        Guid reportId,
        string? note,
        ReportStatus targetStatus,
        ModerationAction action,
        CancellationToken cancellationToken)
    {
        var adminResult = await EnsureAdminAsync(cancellationToken);
        if (!adminResult.IsSuccess)
        {
            return ServiceResult<AdminReportRowResponse>.Failure(adminResult.Error!);
        }

        var admin = adminResult.Value!;

        var report = await _store.FindByIdAsync(reportId, cancellationToken);
        if (report is null)
        {
            return Failure<AdminReportRowResponse>(ErrorCodes.ReportNotFound, "Report was not found.");
        }

        if (report.Status != targetStatus)
        {
            var trimmedNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
            var now = DateTime.UtcNow;

            report.Status = targetStatus;
            report.ResolvedAt = now;
            report.ResolvedByUserId = admin.Id;
            report.ResolutionNote = trimmedNote;

            await _store.SaveChangesAsync(cancellationToken);

            await _moderationLogStore.AppendAsync(new ModerationLogEntry
            {
                Id = Guid.NewGuid(),
                ActorUserId = admin.Id,
                Action = action,
                TargetType = ModerationTargetType.Report,
                TargetId = report.Id,
                TargetLabel = Truncate(report.TargetLabel),
                DetailJson = JsonSerializer.Serialize(new { note = TruncateOrNull(trimmedNote) }, DetailJsonOptions),
                CreatedAt = now
            }, cancellationToken);
        }
        // else: already at the target status — idempotent no-op, same convention as
        // AdminUsersService.SuspendAsync/ReactivateAsync.

        var row = (await MapRowsAsync(new[] { report }, cancellationToken)).Single();
        return ServiceResult<AdminReportRowResponse>.Success(row);
    }

    // Returns the authenticated admin User or a failure result. Mirrors
    // AdminListingsService.EnsureAdminAsync / AdminUsersService.EnsureAdminAsync exactly.
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

    // Resolves TargetImageUrl (listing primary image / user avatar / null for Message) in two
    // batched queries — never one query per row — same convention as
    // AdminListingsService/AdminListingsStore.GetOwnerListingStatsAsync.
    private async Task<List<AdminReportRowResponse>> MapRowsAsync(
        IReadOnlyCollection<Report> reports, CancellationToken cancellationToken)
    {
        var listingIds = reports
            .Where(report => report.TargetType == ReportTargetType.Listing)
            .Select(report => report.TargetId)
            .Distinct()
            .ToList();
        var userIds = reports
            .Where(report => report.TargetType == ReportTargetType.User)
            .Select(report => report.TargetId)
            .Distinct()
            .ToList();

        var listingImages = listingIds.Count == 0
            ? new Dictionary<Guid, string?>()
            : await _store.GetListingPrimaryImageUrlsAsync(listingIds, cancellationToken);
        var userAvatars = userIds.Count == 0
            ? new Dictionary<Guid, string?>()
            : await _store.GetUserAvatarUrlsAsync(userIds, cancellationToken);

        return reports.Select(report => new AdminReportRowResponse
        {
            Id = report.Id,
            TargetType = report.TargetType,
            TargetId = report.TargetId,
            TargetLabel = report.TargetLabel,
            TargetImageUrl = report.TargetType switch
            {
                ReportTargetType.Listing => listingImages.GetValueOrDefault(report.TargetId),
                ReportTargetType.User => userAvatars.GetValueOrDefault(report.TargetId),
                _ => null
            },
            ReasonCode = report.ReasonCode,
            Detail = report.Detail,
            Severity = report.Severity,
            Status = report.Status,
            CreatedAt = report.CreatedAt,
            ReporterId = report.ReporterUserId,
            ReporterFirstName = report.Reporter.FirstName,
            ReporterLastName = report.Reporter.LastName,
            ReporterEmail = report.Reporter.Email,
            ReporterAvatarUrl = report.Reporter.AvatarUrl,
            ResolvedAt = report.ResolvedAt,
            ResolutionNote = report.ResolutionNote
        }).ToList();
    }

    // "open" | "resolved" | "dismissed" | "all", case-insensitive; anything else (including
    // null/empty) defaults to "open" — see AdminReportQueueFilter.Status.
    private static ReportStatus? ParseStatus(string? status) => status?.Trim().ToLowerInvariant() switch
    {
        "resolved" => ReportStatus.Resolved,
        "dismissed" => ReportStatus.Dismissed,
        "all" => null,
        _ => ReportStatus.Open
    };

    // "listing" | "user" | "message", case-insensitive; anything else is invalid — same parsing
    // convention as ReportsService.ParseTargetType on the submission endpoint. Unlike ParseStatus,
    // there is no "default" fallback: an unrecognised value here is a 400, not a silent default,
    // because the caller only reaches this method when they explicitly supplied a non-blank value.
    private static ReportTargetType? ParseTargetType(string value) => value.Trim().ToLowerInvariant() switch
    {
        "listing" => ReportTargetType.Listing,
        "user" => ReportTargetType.User,
        "message" => ReportTargetType.Message,
        _ => null
    };

    private static string Truncate(string value) =>
        value.Length <= MaxTargetLabelLength ? value : value[..MaxTargetLabelLength];

    // Bounds the note that goes into DetailJson — see MaxDetailNoteLength. Truncates the raw note
    // itself (before serialisation) so the emitted JSON always stays well-formed.
    private static string? TruncateOrNull(string? value) =>
        value is null || value.Length <= MaxDetailNoteLength ? value : value[..MaxDetailNoteLength];

    private static ServiceResult<T> Failure<T>(string code, string message) =>
        ServiceResult<T>.Failure(new ServiceError { Code = code, Message = message });
}
