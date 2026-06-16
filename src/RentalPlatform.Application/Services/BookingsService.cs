using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.Common;
using RentalPlatform.Application.DTOs;
using RentalPlatform.Domain;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Application.Services;

public sealed class BookingsService : IBookingsService
{
    private static class ErrorCodes
    {
        public const string Unauthenticated = "booking.unauthenticated";
        public const string UserBlocked = "booking.user_blocked";
        public const string ListingNotFound = "booking.listing_not_found";
        public const string ListingNotApproved = "booking.listing_not_approved";
        public const string OwnListingForbidden = "booking.own_listing_forbidden";
        public const string InvalidDates = "booking.invalid_dates";
        public const string Overlap = "booking.overlap";
        public const string BookingNotFound = "booking.not_found";
        public const string BookingForbidden = "booking.forbidden";
        public const string BookingNotPending = "booking.not_pending";
        public const string NotCancellable = "booking.not_cancellable";
        public const string NotCompletable = "booking.not_completable";
    }

    private readonly ICurrentUserContext _currentUserContext;
    private readonly IBookingsStore _bookingsStore;

    public BookingsService(ICurrentUserContext currentUserContext, IBookingsStore bookingsStore)
    {
        _currentUserContext = currentUserContext;
        _bookingsStore = bookingsStore;
    }

    public async Task<ServiceResult<BookingResponse>> CreateAsync(
        CreateBookingRequest request,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        await _bookingsStore.ExpirePendingAsync(now, cancellationToken);

        var userResult = await GetCurrentUserAsync(cancellationToken);
        if (!userResult.IsSuccess || userResult.Value is null)
        {
            return ServiceResult<BookingResponse>.Failure(userResult.Error!);
        }

        var renter = userResult.Value;
        var listing = await _bookingsStore.FindListingByIdAsync(request.ListingId, cancellationToken);
        if (listing is null)
        {
            return Failure<BookingResponse>(ErrorCodes.ListingNotFound, "Listing was not found.");
        }

        if (listing.Status != ListingStatus.Approved)
        {
            return Failure<BookingResponse>(ErrorCodes.ListingNotApproved, "Only approved listings can be booked.");
        }

        if (listing.OwnerId == renter.Id)
        {
            return Failure<BookingResponse>(ErrorCodes.OwnListingForbidden, "You cannot book your own listing.");
        }

        var dateValidation = ValidateDates(request.StartDate, request.EndDate, now);
        if (dateValidation is not null)
        {
            return Failure<BookingResponse>(ErrorCodes.InvalidDates, dateValidation);
        }

        var inclusiveDays = request.EndDate.DayNumber - request.StartDate.DayNumber + 1;
        var booking = new Booking
        {
            Id = Guid.NewGuid(),
            ListingId = listing.Id,
            RenterId = renter.Id,
            StartDate = request.StartDate,
            EndDate = request.EndDate,
            TotalPrice = inclusiveDays * listing.PricePerDay,
            Status = BookingStatus.Pending,
            ExpiresAt = now.AddHours(24),
            CreatedAt = now,
            UpdatedAt = now
        };

        // Atomic overlap-then-insert. Blocks if any Pending OR Approved booking covers the
        // requested range — preventing both duplicate-request and racing concurrent creates.
        var added = await _bookingsStore.TryCreateBookingAsync(booking, cancellationToken);
        if (!added)
        {
            return Failure<BookingResponse>(ErrorCodes.Overlap, "The selected dates overlap with an existing booking request.");
        }

        // Attach the already-loaded listing so MapBooking can read listing fields without a second DB query.
        // listing.Images is an empty collection here (not included), so PrimaryImageUrl will be null.
        booking.Listing = listing;
        return ServiceResult<BookingResponse>.Success(MapBooking(booking));
    }

    public async Task<ServiceResult<IReadOnlyCollection<BookingResponse>>> GetMineAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        await _bookingsStore.ExpirePendingAsync(now, cancellationToken);

        var userResult = await GetCurrentUserAsync(cancellationToken);
        if (!userResult.IsSuccess || userResult.Value is null)
        {
            return ServiceResult<IReadOnlyCollection<BookingResponse>>.Failure(userResult.Error!);
        }

        var bookings = await _bookingsStore.GetRenterBookingsAsync(userResult.Value.Id, cancellationToken);
        var response = bookings
            .OrderByDescending(booking => booking.CreatedAt)
            .Select(MapBooking)
            .ToList();

        return ServiceResult<IReadOnlyCollection<BookingResponse>>.Success(response);
    }

    public async Task<ServiceResult<IReadOnlyCollection<BookingRequestResponse>>> GetOwnerRequestsAsync(
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        await _bookingsStore.ExpirePendingAsync(now, cancellationToken);

        var userResult = await GetCurrentUserAsync(cancellationToken);
        if (!userResult.IsSuccess || userResult.Value is null)
        {
            return ServiceResult<IReadOnlyCollection<BookingRequestResponse>>.Failure(userResult.Error!);
        }

        var requests = await _bookingsStore.GetOwnerBookingRequestsAsync(userResult.Value.Id, cancellationToken);
        var response = requests
            .OrderByDescending(booking => booking.CreatedAt)
            .Select(MapBookingRequest)
            .ToList();

        return ServiceResult<IReadOnlyCollection<BookingRequestResponse>>.Success(response);
    }

    public Task<ServiceResult<BookingRequestResponse>> ApproveAsync(Guid id, CancellationToken cancellationToken = default) =>
        UpdateOwnerDecisionAsync(id, BookingStatus.Approved, cancellationToken);

    public Task<ServiceResult<BookingRequestResponse>> RejectAsync(Guid id, CancellationToken cancellationToken = default) =>
        UpdateOwnerDecisionAsync(id, BookingStatus.Rejected, cancellationToken);

    public async Task<ServiceResult<BookingResponse>> CancelAsync(Guid bookingId, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        await _bookingsStore.ExpirePendingAsync(now, cancellationToken);

        var userResult = await GetCurrentUserAsync(cancellationToken);
        if (!userResult.IsSuccess || userResult.Value is null)
        {
            return ServiceResult<BookingResponse>.Failure(userResult.Error!);
        }

        var renter = userResult.Value;
        var booking = await _bookingsStore.FindBookingWithRelationsByIdAsync(bookingId, cancellationToken);
        if (booking is null)
        {
            return Failure<BookingResponse>(ErrorCodes.BookingNotFound, "Booking was not found.");
        }

        if (booking.RenterId != renter.Id)
        {
            return Failure<BookingResponse>(ErrorCodes.BookingForbidden, "Only the renter can cancel this booking.");
        }

        if (!BookingStatusTransitions.CanTransition(booking.Status, BookingStatus.Cancelled))
        {
            return Failure<BookingResponse>(
                ErrorCodes.NotCancellable,
                $"Bookings in status '{booking.Status}' cannot be cancelled.");
        }

        // Approved bookings can only be cancelled before the rental start date.
        // (No refund/payment logic; cancellation after start is intentionally blocked here.)
        if (booking.Status == BookingStatus.Approved)
        {
            var today = DateOnly.FromDateTime(now);
            if (booking.StartDate <= today)
            {
                return Failure<BookingResponse>(
                    ErrorCodes.NotCancellable,
                    "Approved bookings cannot be cancelled on or after the rental start date.");
            }
        }

        booking.Status = BookingStatus.Cancelled;
        booking.UpdatedAt = now;
        await _bookingsStore.SaveChangesAsync(cancellationToken);

        return ServiceResult<BookingResponse>.Success(MapBooking(booking));
    }

    public async Task<ServiceResult<BookingResponse>> CompleteAsync(Guid bookingId, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        await _bookingsStore.ExpirePendingAsync(now, cancellationToken);

        var userResult = await GetCurrentUserAsync(cancellationToken);
        if (!userResult.IsSuccess || userResult.Value is null)
        {
            return ServiceResult<BookingResponse>.Failure(userResult.Error!);
        }

        var owner = userResult.Value;
        var booking = await _bookingsStore.FindBookingWithRelationsByIdAsync(bookingId, cancellationToken);
        if (booking is null)
        {
            return Failure<BookingResponse>(ErrorCodes.BookingNotFound, "Booking was not found.");
        }

        // Completion is an owner-only action: the owner confirms the toy was returned.
        if (booking.Listing.OwnerId != owner.Id)
        {
            return Failure<BookingResponse>(ErrorCodes.BookingForbidden, "Only the listing owner can complete this booking.");
        }

        if (!BookingStatusTransitions.CanTransition(booking.Status, BookingStatus.Completed))
        {
            return Failure<BookingResponse>(
                ErrorCodes.NotCompletable,
                $"Bookings in status '{booking.Status}' cannot be completed.");
        }

        booking.Status = BookingStatus.Completed;
        booking.UpdatedAt = now;
        await _bookingsStore.SaveChangesAsync(cancellationToken);

        return ServiceResult<BookingResponse>.Success(MapBooking(booking));
    }

    private async Task<ServiceResult<BookingRequestResponse>> UpdateOwnerDecisionAsync(
        Guid bookingId,
        BookingStatus decision,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        await _bookingsStore.ExpirePendingAsync(now, cancellationToken);

        var userResult = await GetCurrentUserAsync(cancellationToken);
        if (!userResult.IsSuccess || userResult.Value is null)
        {
            return ServiceResult<BookingRequestResponse>.Failure(userResult.Error!);
        }

        var owner = userResult.Value;
        var booking = await _bookingsStore.FindBookingWithRelationsByIdAsync(bookingId, cancellationToken);
        if (booking is null)
        {
            return Failure<BookingRequestResponse>(ErrorCodes.BookingNotFound, "Booking was not found.");
        }

        if (booking.Listing.OwnerId != owner.Id)
        {
            return Failure<BookingRequestResponse>(ErrorCodes.BookingForbidden, "Only listing owner can manage this booking request.");
        }

        // Defensive: ExpirePendingAsync already promoted any past-expiry rows to Expired, but
        // guard against a race where ExpiresAt slipped past 'now' between the sweep and the read.
        if (!BookingStatusTransitions.CanTransition(booking.Status, decision) || booking.ExpiresAt <= now)
        {
            return Failure<BookingRequestResponse>(ErrorCodes.BookingNotPending, "Only valid pending booking requests can be managed.");
        }

        if (decision == BookingStatus.Approved)
        {
            var hasOverlap = await _bookingsStore.HasApprovedOverlapAsync(
                booking.ListingId,
                booking.StartDate,
                booking.EndDate,
                booking.Id,
                cancellationToken);

            if (hasOverlap)
            {
                return Failure<BookingRequestResponse>(ErrorCodes.Overlap, "Cannot approve booking due to overlap with approved booking.");
            }
        }

        booking.Status = decision;
        booking.UpdatedAt = now;
        await _bookingsStore.SaveChangesAsync(cancellationToken);

        return ServiceResult<BookingRequestResponse>.Success(MapBookingRequest(booking));
    }

    private async Task<ServiceResult<User>> GetCurrentUserAsync(CancellationToken cancellationToken)
    {
        if (_currentUserContext.UserId is not { } userId)
        {
            return Failure<User>(ErrorCodes.Unauthenticated, "Current user is not authenticated.");
        }

        var user = await _bookingsStore.FindUserByIdAsync(userId, cancellationToken);
        if (user is null)
        {
            return Failure<User>(ErrorCodes.Unauthenticated, "Current user is not authenticated.");
        }

        if (user.IsBlocked)
        {
            return Failure<User>(ErrorCodes.UserBlocked, "Blocked users cannot perform booking actions.");
        }

        return ServiceResult<User>.Success(user);
    }

    private static string? ValidateDates(DateOnly startDate, DateOnly endDate, DateTime utcNow)
    {
        if (endDate < startDate)
        {
            return "EndDate must be greater than or equal to StartDate.";
        }

        var today = DateOnly.FromDateTime(utcNow);
        if (startDate < today)
        {
            return "StartDate cannot be in the past.";
        }

        return null;
    }

    private static BookingResponse MapBooking(Booking booking) => new()
    {
        Id = booking.Id,
        ListingId = booking.ListingId,
        ListingTitle = booking.Listing.Title,
        ListingPrimaryImageUrl = booking.Listing.Images
            .OrderByDescending(image => image.IsPrimary)
            .ThenBy(image => image.SortOrder)
            .Select(image => image.Url)
            .FirstOrDefault(),
        Currency = booking.Listing.Currency,
        PricePerDay = booking.Listing.PricePerDay,
        DepositAmount = booking.Listing.DepositAmount,
        StartDate = booking.StartDate,
        EndDate = booking.EndDate,
        TotalPrice = booking.TotalPrice,
        Status = booking.Status,
        ExpiresAt = booking.ExpiresAt,
        CreatedAt = booking.CreatedAt,
        UpdatedAt = booking.UpdatedAt
    };

    private static BookingRequestResponse MapBookingRequest(Booking booking) => new()
    {
        Id = booking.Id,
        ListingId = booking.ListingId,
        ListingTitle = booking.Listing.Title,
        ListingPrimaryImageUrl = booking.Listing.Images
            .OrderByDescending(image => image.IsPrimary)
            .ThenBy(image => image.SortOrder)
            .Select(image => image.Url)
            .FirstOrDefault(),
        Currency = booking.Listing.Currency,
        RenterId = booking.RenterId,
        RenterEmail = booking.Renter.Email,
        RenterFirstName = booking.Renter.FirstName,
        RenterLastName = booking.Renter.LastName,
        StartDate = booking.StartDate,
        EndDate = booking.EndDate,
        TotalPrice = booking.TotalPrice,
        Status = booking.Status,
        ExpiresAt = booking.ExpiresAt,
        CreatedAt = booking.CreatedAt,
        UpdatedAt = booking.UpdatedAt
    };

    private static ServiceResult<T> Failure<T>(string code, string message) =>
        ServiceResult<T>.Failure(new ServiceError
        {
            Code = code,
            Message = message
        });
}
