using System.Globalization;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.Common;
using RentalPlatform.Application.DTOs;
using RentalPlatform.Domain;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Application.Services;

/// <summary>Admin-only read of the aggregated demand counters (P3-3).</summary>
public sealed class AdminDemandService : IAdminDemandService
{
    private static class ErrorCodes
    {
        public const string Unauthenticated = "admin.unauthenticated";
        public const string Forbidden = "admin.forbidden";
        public const string RangeInvalid = "admin.demand_range_invalid";
    }

    private const string DateFormat = "yyyy-MM-dd";
    private const int MaxRangeDays = 366;
    private const int DefaultRangeDays = 30;

    private readonly ICurrentUserContext _currentUserContext;
    private readonly IAdminDemandStore _store;
    private readonly TimeProvider _timeProvider;

    public AdminDemandService(
        ICurrentUserContext currentUserContext,
        IAdminDemandStore store,
        TimeProvider timeProvider)
    {
        _currentUserContext = currentUserContext;
        _store = store;
        _timeProvider = timeProvider;
    }

    public async Task<ServiceResult<DemandAnalyticsResponse>> GetDemandAsync(
        string? from, string? to, CancellationToken cancellationToken = default)
    {
        var adminResult = await EnsureAdminAsync(cancellationToken);
        if (!adminResult.IsSuccess)
        {
            return ServiceResult<DemandAnalyticsResponse>.Failure(adminResult.Error!);
        }

        var hasFrom = !string.IsNullOrWhiteSpace(from);
        var hasTo = !string.IsNullOrWhiteSpace(to);
        if (hasFrom != hasTo)
        {
            return Invalid("Supply both from and to, or neither.");
        }

        DateOnly fromDay;
        DateOnly toDay;
        if (!hasFrom)
        {
            toDay = YerevanCalendar.DateOf(_timeProvider.GetUtcNow());
            fromDay = toDay.AddDays(-(DefaultRangeDays - 1));
        }
        else
        {
            if (!DateOnly.TryParseExact(from!.Trim(), DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out fromDay)
                || !DateOnly.TryParseExact(to!.Trim(), DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out toDay))
            {
                return Invalid("from and to must be dates in the form yyyy-MM-dd.");
            }

            if (fromDay > toDay)
            {
                return Invalid("from must not be after to.");
            }

            if (toDay.DayNumber - fromDay.DayNumber + 1 > MaxRangeDays)
            {
                return Invalid($"The range must not exceed {MaxRangeDays} days.");
            }
        }

        var rows = await _store.GetRowsAsync(fromDay, toDay, cancellationToken);
        return ServiceResult<DemandAnalyticsResponse>.Success(new DemandAnalyticsResponse
        {
            From = fromDay,
            To = toDay,
            Rows = rows
        });
    }

    private static ServiceResult<DemandAnalyticsResponse> Invalid(string message) =>
        Failure<DemandAnalyticsResponse>(ErrorCodes.RangeInvalid, message);

    // Same defence-in-depth check as AdminOverviewService.EnsureAdminAsync.
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
