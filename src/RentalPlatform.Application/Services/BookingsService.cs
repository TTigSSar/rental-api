using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.Common;
using RentalPlatform.Application.DTOs;
using RentalPlatform.Domain;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Application.Services;

public sealed class BookingsService : IBookingsService
{
    // Upper bound on a single rental window (inclusive days). Guards against absurd requests
    // (e.g. a multi-year booking) that would lock a listing's calendar and balloon TotalPrice.
    private const int MaxRentalDays = 90;

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
        public const string NotActivatable = "booking.not_activatable";
        public const string NotCompletable = "booking.not_completable";
        public const string OwnerOnlyAction = "booking.owner_only";
    }

    private readonly ICurrentUserContext _currentUserContext;
    private readonly IBookingsStore _bookingsStore;
    private readonly INotificationEmitter _notificationEmitter;
    private readonly IChatSystemMessageEmitter _chatSystemMessageEmitter;
    private readonly IReviewsStore _reviewsStore;
    private readonly IConversationsStore _conversationsStore;

    public BookingsService(
        ICurrentUserContext currentUserContext,
        IBookingsStore bookingsStore,
        INotificationEmitter notificationEmitter,
        IChatSystemMessageEmitter chatSystemMessageEmitter,
        IReviewsStore reviewsStore,
        IConversationsStore conversationsStore)
    {
        _currentUserContext = currentUserContext;
        _bookingsStore = bookingsStore;
        _notificationEmitter = notificationEmitter;
        _chatSystemMessageEmitter = chatSystemMessageEmitter;
        _reviewsStore = reviewsStore;
        _conversationsStore = conversationsStore;
    }

    public async Task<ServiceResult<BookingResponse>> CreateAsync(
        CreateBookingRequest request,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;

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
            // NOTE: assumes a per-DAY rate. Listings now carry a PriceUnit (Hourly/Daily/Weekly/
            // Monthly/Yearly); once non-daily units are bookable this multiplication must convert
            // the window to the listing's unit instead of multiplying inclusive days by the amount.
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

        // Best-effort: notify the owner of the new request (never breaks the booking).
        await _notificationEmitter.BookingRequestedAsync(booking, renter, listing, cancellationToken);

        // Best-effort: drop a "Booking requested." system line into the booking's chat thread.
        await _chatSystemMessageEmitter.BookingRequestedAsync(booking, cancellationToken);

        return ServiceResult<BookingResponse>.Success(MapBooking(booking));
    }

    public async Task<ServiceResult<IReadOnlyCollection<BookingResponse>>> GetMineAsync(CancellationToken cancellationToken = default)
    {
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
        UpdateOwnerDecisionAsync(id, BookingStatus.Approved, null, cancellationToken);

    public Task<ServiceResult<BookingRequestResponse>> RejectAsync(Guid id, string? reason = null, CancellationToken cancellationToken = default) =>
        UpdateOwnerDecisionAsync(id, BookingStatus.Rejected, reason, cancellationToken);

    public async Task<ServiceResult<BookingResponse>> CancelAsync(Guid bookingId, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;

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

    public async Task<ServiceResult<BookingDetailResponse>> GetByIdAsync(Guid bookingId, CancellationToken cancellationToken = default)
    {
        var ctx = await ResolveDetailAsync(bookingId, cancellationToken);
        if (!ctx.IsSuccess || ctx.Value is null)
        {
            return ServiceResult<BookingDetailResponse>.Failure(ctx.Error!);
        }

        return ServiceResult<BookingDetailResponse>.Success(MapBookingDetail(ctx.Value.Booking, ctx.Value.CallerParty));
    }

    public async Task<ServiceResult<BookingDetailResponse>> MarkActiveAsync(Guid bookingId, CancellationToken cancellationToken = default)
    {
        var ctx = await ResolveDetailAsync(bookingId, cancellationToken);
        if (!ctx.IsSuccess || ctx.Value is null)
        {
            return ServiceResult<BookingDetailResponse>.Failure(ctx.Error!);
        }

        var (booking, callerParty) = ctx.Value;
        if (callerParty != BookingParty.Owner)
        {
            return Failure<BookingDetailResponse>(ErrorCodes.OwnerOnlyAction, "Only the owner can mark the toy as handed over.");
        }

        if (!BookingStatusTransitions.CanTransition(booking.Status, BookingStatus.Active))
        {
            return Failure<BookingDetailResponse>(
                ErrorCodes.NotActivatable,
                $"Bookings in status '{booking.Status}' cannot be marked active.");
        }

        var now = DateTime.UtcNow;
        booking.Status = BookingStatus.Active;
        booking.ActiveAt = now;
        booking.UpdatedAt = now;
        await _bookingsStore.SaveChangesAsync(cancellationToken);

        // Best-effort: drop a "Toy handed over…" system line into the booking's chat thread.
        await _chatSystemMessageEmitter.BookingHandedOverAsync(booking, cancellationToken);

        return ServiceResult<BookingDetailResponse>.Success(MapBookingDetail(booking, callerParty));
    }

    public async Task<ServiceResult<BookingDetailResponse>> CompleteAsync(Guid bookingId, CancellationToken cancellationToken = default)
    {
        var ctx = await ResolveDetailAsync(bookingId, cancellationToken);
        if (!ctx.IsSuccess || ctx.Value is null)
        {
            return ServiceResult<BookingDetailResponse>.Failure(ctx.Error!);
        }

        var (booking, callerParty) = ctx.Value;
        if (callerParty != BookingParty.Owner)
        {
            return Failure<BookingDetailResponse>(ErrorCodes.OwnerOnlyAction, "Only the owner can complete the rental.");
        }

        if (!BookingStatusTransitions.CanTransition(booking.Status, BookingStatus.Completed))
        {
            return Failure<BookingDetailResponse>(
                ErrorCodes.NotCompletable,
                $"Bookings in status '{booking.Status}' cannot be completed.");
        }

        var now = DateTime.UtcNow;
        booking.Status = BookingStatus.Completed;
        booking.CompletedAt = now;
        booking.UpdatedAt = now;
        await _bookingsStore.SaveChangesAsync(cancellationToken);

        // Best-effort: drop a "The rental is complete." system line into the booking's chat thread.
        await _chatSystemMessageEmitter.BookingCompletedAsync(booking, cancellationToken);

        // Covers the (unusual) ordering where both party reviews were already submitted before
        // completion — normally the last review submission (ReviewsService) is what closes the
        // conversation for the ADR-001 read-only lock. Best-effort, mirrors the emitter above.
        var hasOwnerReview = await _reviewsStore.HasOwnerReviewAsync(booking.Id, cancellationToken);
        var hasRenterReview = await _reviewsStore.HasRenterReviewAsync(booking.Id, cancellationToken);
        if (hasOwnerReview && hasRenterReview)
        {
            await _conversationsStore.CloseForBookingAsync(booking.Id, now, cancellationToken);
        }

        return ServiceResult<BookingDetailResponse>.Success(MapBookingDetail(booking, callerParty));
    }

    // Loads a booking detail, authenticates the caller, and resolves which party they are.
    // Fails with forbidden if the caller is neither the renter nor the listing owner.
    private async Task<ServiceResult<BookingDetailContext>> ResolveDetailAsync(
        Guid bookingId, CancellationToken cancellationToken)
    {
        var userResult = await GetCurrentUserAsync(cancellationToken);
        if (!userResult.IsSuccess || userResult.Value is null)
        {
            return ServiceResult<BookingDetailContext>.Failure(userResult.Error!);
        }

        var caller = userResult.Value;
        var booking = await _bookingsStore.FindBookingDetailByIdAsync(bookingId, cancellationToken);
        if (booking is null)
        {
            return Failure<BookingDetailContext>(ErrorCodes.BookingNotFound, "Booking was not found.");
        }

        BookingParty callerParty;
        if (booking.RenterId == caller.Id)
        {
            callerParty = BookingParty.Renter;
        }
        else if (booking.Listing.OwnerId == caller.Id)
        {
            callerParty = BookingParty.Owner;
        }
        else
        {
            return Failure<BookingDetailContext>(ErrorCodes.BookingForbidden, "Only the renter or owner of this booking can view it.");
        }

        return ServiceResult<BookingDetailContext>.Success(new BookingDetailContext(booking, callerParty));
    }

    private sealed record BookingDetailContext(Booking Booking, BookingParty CallerParty);

    private async Task<ServiceResult<BookingRequestResponse>> UpdateOwnerDecisionAsync(
        Guid bookingId,
        BookingStatus decision,
        string? rejectionReason,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;

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

        booking.Status = decision;
        booking.UpdatedAt = now;

        if (decision == BookingStatus.Approved)
        {
            booking.ApprovedAt = now;

            // Atomic overlap-then-approve under SERIALIZABLE: blocks concurrent approvals of
            // overlapping pending requests from both committing (double-booking guard).
            var approved = await _bookingsStore.TryApproveBookingAsync(booking, cancellationToken);
            if (!approved)
            {
                return Failure<BookingRequestResponse>(ErrorCodes.Overlap, "Cannot approve booking due to overlap with approved booking.");
            }
        }
        else
        {
            if (decision == BookingStatus.Rejected)
            {
                var trimmed = rejectionReason?.Trim();
                booking.RejectionReason = string.IsNullOrEmpty(trimmed) ? null : trimmed;
            }
            await _bookingsStore.SaveChangesAsync(cancellationToken);
        }

        // Best-effort: notify the renter of the owner's decision.
        if (decision == BookingStatus.Approved)
        {
            await _notificationEmitter.BookingApprovedAsync(booking, owner, cancellationToken);

            // Best-effort: drop an "approved" system line into the booking's chat thread.
            // (Declined has no ChatSystemKind — no system message on rejection.)
            await _chatSystemMessageEmitter.BookingApprovedAsync(booking, cancellationToken);
        }
        else if (decision == BookingStatus.Rejected)
        {
            await _notificationEmitter.BookingDeclinedAsync(booking, owner, cancellationToken);
        }

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

        var inclusiveDays = endDate.DayNumber - startDate.DayNumber + 1;
        if (inclusiveDays > MaxRentalDays)
        {
            return $"A booking cannot exceed {MaxRentalDays} days.";
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
        OwnerFirstName = booking.Listing.Owner?.FirstName ?? string.Empty,
        OwnerLastName = booking.Listing.Owner?.LastName ?? string.Empty,
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

    private static BookingDetailResponse MapBookingDetail(Booking booking, BookingParty callerParty)
    {
        var listing = booking.Listing;
        var counterparty = callerParty == BookingParty.Renter ? listing.Owner : booking.Renter;

        // Address and phone are revealed only once the booking is at least Approved.
        var contactRevealed = booking.Status is BookingStatus.Approved
            or BookingStatus.Active
            or BookingStatus.Completed;

        return new BookingDetailResponse
        {
            Id = booking.Id,
            Status = booking.Status,
            Role = callerParty == BookingParty.Renter ? "renter" : "owner",

            ListingId = listing.Id,
            ListingTitle = listing.Title,
            ListingPrimaryImageUrl = listing.Images
                .OrderByDescending(image => image.IsPrimary)
                .ThenBy(image => image.SortOrder)
                .Select(image => image.Url)
                .FirstOrDefault(),
            CategoryName = listing.Category?.Name,
            Condition = listing.Condition,
            City = listing.City,
            Country = listing.Country,
            AddressLine = contactRevealed ? listing.AddressLine : null,

            Currency = listing.Currency,
            PricePerDay = listing.PricePerDay,
            DepositAmount = listing.DepositAmount,
            TotalPrice = booking.TotalPrice,
            StartDate = booking.StartDate,
            EndDate = booking.EndDate,

            CreatedAt = booking.CreatedAt,
            ApprovedAt = booking.ApprovedAt,
            ActiveAt = booking.ActiveAt,
            CompletedAt = booking.CompletedAt,
            ExpiresAt = booking.ExpiresAt,

            RejectionReason = booking.RejectionReason,

            CounterpartyId = counterparty.Id,
            CounterpartyFirstName = counterparty.FirstName,
            CounterpartyLastName = counterparty.LastName,
            CounterpartyAvatarUrl = counterparty.AvatarUrl,
            CounterpartyPhoneNumber = contactRevealed ? counterparty.PhoneNumber : null
        };
    }

    private static ServiceResult<T> Failure<T>(string code, string message) =>
        ServiceResult<T>.Failure(new ServiceError
        {
            Code = code,
            Message = message
        });
}
