using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.Common;
using RentalPlatform.Application.DTOs;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Application.Services;

public sealed class ReviewsService : IReviewsService
{
    // Aggregates (averages, distribution) are hidden until a listing/user has at
    // least this many reviews. Comments are always shown.
    public const int MinReviewsForAggregate = 2;

    private static class ErrorCodes
    {
        public const string Unauthenticated  = "review.unauthenticated";
        public const string BookingNotFound  = "review.booking_not_found";
        public const string NotCompleted     = "review.booking_not_completed";
        public const string Forbidden        = "review.forbidden";
        public const string AlreadySubmitted = "review.already_submitted";
    }

    private readonly ICurrentUserContext _currentUserContext;
    private readonly IReviewsStore _reviewsStore;

    public ReviewsService(ICurrentUserContext currentUserContext, IReviewsStore reviewsStore)
    {
        _currentUserContext = currentUserContext;
        _reviewsStore = reviewsStore;
    }

    public async Task<ServiceResult<BookingReviewStatusResponse>> SubmitToyReviewAsync(
        CreateToyReviewRequest request, CancellationToken cancellationToken = default)
    {
        var ctx = await ResolveAsync(request.BookingId, cancellationToken);
        if (!ctx.IsSuccess || ctx.Value is null) return ServiceResult<BookingReviewStatusResponse>.Failure(ctx.Error!);
        var (callerId, booking) = (ctx.Value.CallerId, ctx.Value.Booking);

        if (callerId != booking.RenterId)
        {
            return Failure(ErrorCodes.Forbidden, "Only the renter can review the toy.");
        }

        if (await _reviewsStore.HasToyReviewAsync(booking.Id, cancellationToken))
        {
            return Failure(ErrorCodes.AlreadySubmitted, "You have already reviewed this toy.");
        }

        await _reviewsStore.AddToyReviewAsync(new ToyReview
        {
            Id = Guid.NewGuid(),
            BookingId = booking.Id,
            ListingId = booking.ListingId,
            ReviewerId = callerId,
            OverallRating = request.OverallRating,
            ConditionRating = request.ConditionRating,
            CleanlinessRating = request.CleanlinessRating,
            ValueForMoneyRating = request.ValueForMoneyRating,
            FunPlayValueRating = request.FunPlayValueRating,
            DescriptionAccuracyRating = request.DescriptionAccuracyRating,
            Comment = NormalizeComment(request.Comment),
            CreatedAt = DateTime.UtcNow
        }, cancellationToken);

        return await BuildStatusAsync(callerId, booking, cancellationToken);
    }

    public async Task<ServiceResult<BookingReviewStatusResponse>> SubmitOwnerReviewAsync(
        CreateOwnerReviewRequest request, CancellationToken cancellationToken = default)
    {
        var ctx = await ResolveAsync(request.BookingId, cancellationToken);
        if (!ctx.IsSuccess || ctx.Value is null) return ServiceResult<BookingReviewStatusResponse>.Failure(ctx.Error!);
        var (callerId, booking) = (ctx.Value.CallerId, ctx.Value.Booking);

        if (callerId != booking.RenterId)
        {
            return Failure(ErrorCodes.Forbidden, "Only the renter can review the owner.");
        }

        if (await _reviewsStore.HasOwnerReviewAsync(booking.Id, cancellationToken))
        {
            return Failure(ErrorCodes.AlreadySubmitted, "You have already reviewed this owner.");
        }

        await _reviewsStore.AddOwnerReviewAsync(new OwnerReview
        {
            Id = Guid.NewGuid(),
            BookingId = booking.Id,
            OwnerId = booking.Listing.OwnerId,
            ReviewerId = callerId,
            CommunicationRating = request.CommunicationRating,
            PickupHandoverRating = request.PickupHandoverRating,
            FriendlinessRating = request.FriendlinessRating,
            Comment = NormalizeComment(request.Comment),
            CreatedAt = DateTime.UtcNow
        }, cancellationToken);

        return await BuildStatusAsync(callerId, booking, cancellationToken);
    }

    public async Task<ServiceResult<BookingReviewStatusResponse>> SubmitRenterReviewAsync(
        CreateRenterReviewRequest request, CancellationToken cancellationToken = default)
    {
        var ctx = await ResolveAsync(request.BookingId, cancellationToken);
        if (!ctx.IsSuccess || ctx.Value is null) return ServiceResult<BookingReviewStatusResponse>.Failure(ctx.Error!);
        var (callerId, booking) = (ctx.Value.CallerId, ctx.Value.Booking);

        if (callerId != booking.Listing.OwnerId)
        {
            return Failure(ErrorCodes.Forbidden, "Only the owner can review the renter.");
        }

        if (await _reviewsStore.HasRenterReviewAsync(booking.Id, cancellationToken))
        {
            return Failure(ErrorCodes.AlreadySubmitted, "You have already reviewed this renter.");
        }

        await _reviewsStore.AddRenterReviewAsync(new RenterReview
        {
            Id = Guid.NewGuid(),
            BookingId = booking.Id,
            RenterId = booking.RenterId,
            ReviewerId = callerId,
            CommunicationRating = request.CommunicationRating,
            ReturnedOnTimeRating = request.ReturnedOnTimeRating,
            CareOfToyRating = request.CareOfToyRating,
            WouldRentAgainRating = request.WouldRentAgainRating,
            Comment = NormalizeComment(request.Comment),
            CreatedAt = DateTime.UtcNow
        }, cancellationToken);

        return await BuildStatusAsync(callerId, booking, cancellationToken);
    }

    public async Task<ServiceResult<BookingReviewStatusResponse>> GetBookingReviewStatusAsync(
        Guid bookingId, CancellationToken cancellationToken = default)
    {
        if (_currentUserContext.UserId is not { } callerId)
        {
            return Failure(ErrorCodes.Unauthenticated, "Authentication is required.");
        }

        var booking = await _reviewsStore.FindBookingForReviewAsync(bookingId, cancellationToken);
        if (booking is null)
        {
            return Failure(ErrorCodes.BookingNotFound, "Booking was not found.");
        }

        if (callerId != booking.RenterId && callerId != booking.Listing.OwnerId)
        {
            return Failure(ErrorCodes.Forbidden, "Only the renter or owner of this booking can view its review status.");
        }

        return await BuildStatusAsync(callerId, booking, cancellationToken);
    }

    public async Task<ToyReviewSummaryResponse> GetListingToyReviewsAsync(
        Guid listingId, CancellationToken cancellationToken = default)
    {
        var reviews = await _reviewsStore.GetToyReviewsByListingAsync(listingId, cancellationToken);
        var count = reviews.Count;
        var hasAggregate = count >= MinReviewsForAggregate;

        return new ToyReviewSummaryResponse
        {
            ReviewCount = count,
            HasAggregate = hasAggregate,
            OverallAverage = Avg(reviews, r => r.OverallRating),
            ConditionAverage = Avg(reviews, r => r.ConditionRating),
            CleanlinessAverage = Avg(reviews, r => r.CleanlinessRating),
            ValueForMoneyAverage = Avg(reviews, r => r.ValueForMoneyRating),
            FunPlayValueAverage = Avg(reviews, r => r.FunPlayValueRating),
            DescriptionAccuracyAverage = Avg(reviews, r => r.DescriptionAccuracyRating),
            Distribution = Distribution(reviews.Select(r => r.OverallRating)),
            Comments = reviews
                .Where(r => !string.IsNullOrWhiteSpace(r.Comment))
                .Select(r => MapComment(r.Id, r.Reviewer, r.Booking, r.Comment!, r.CreatedAt))
                .ToList()
        };
    }

    public async Task<OwnerReviewSummaryResponse> GetOwnerReviewsAsync(
        Guid ownerId, CancellationToken cancellationToken = default)
    {
        var reviews = await _reviewsStore.GetOwnerReviewsByUserAsync(ownerId, cancellationToken);
        var count = reviews.Count;

        return new OwnerReviewSummaryResponse
        {
            ReviewCount = count,
            HasAggregate = count >= MinReviewsForAggregate,
            OverallAverage = Avg(reviews, r => (r.CommunicationRating + r.PickupHandoverRating + r.FriendlinessRating) / 3.0),
            CommunicationAverage = Avg(reviews, r => r.CommunicationRating),
            PickupHandoverAverage = Avg(reviews, r => r.PickupHandoverRating),
            FriendlinessAverage = Avg(reviews, r => r.FriendlinessRating),
            Distribution = Distribution(reviews.Select(r =>
                RoundToBucket((r.CommunicationRating + r.PickupHandoverRating + r.FriendlinessRating) / 3.0))),
            Comments = reviews
                .Where(r => !string.IsNullOrWhiteSpace(r.Comment))
                .Select(r => MapComment(r.Id, r.Reviewer, r.Booking, r.Comment!, r.CreatedAt))
                .ToList()
        };
    }

    public async Task<RenterReviewSummaryResponse> GetRenterReviewsAsync(
        Guid renterId, CancellationToken cancellationToken = default)
    {
        var reviews = await _reviewsStore.GetRenterReviewsByUserAsync(renterId, cancellationToken);
        var count = reviews.Count;

        return new RenterReviewSummaryResponse
        {
            ReviewCount = count,
            HasAggregate = count >= MinReviewsForAggregate,
            OverallAverage = Avg(reviews, r => (r.CommunicationRating + r.ReturnedOnTimeRating + r.CareOfToyRating + r.WouldRentAgainRating) / 4.0),
            CommunicationAverage = Avg(reviews, r => r.CommunicationRating),
            ReturnedOnTimeAverage = Avg(reviews, r => r.ReturnedOnTimeRating),
            CareOfToyAverage = Avg(reviews, r => r.CareOfToyRating),
            WouldRentAgainAverage = Avg(reviews, r => r.WouldRentAgainRating),
            Distribution = Distribution(reviews.Select(r =>
                RoundToBucket((r.CommunicationRating + r.ReturnedOnTimeRating + r.CareOfToyRating + r.WouldRentAgainRating) / 4.0))),
            Comments = reviews
                .Where(r => !string.IsNullOrWhiteSpace(r.Comment))
                .Select(r => MapComment(r.Id, r.Reviewer, r.Booking, r.Comment!, r.CreatedAt))
                .ToList()
        };
    }

    // --- helpers ---

    private sealed record ReviewContext(Guid CallerId, Booking Booking);

    private async Task<ServiceResult<ReviewContext>> ResolveAsync(
        Guid bookingId, CancellationToken cancellationToken)
    {
        if (_currentUserContext.UserId is not { } callerId)
        {
            return ServiceResult<ReviewContext>.Failure(new ServiceError
            {
                Code = ErrorCodes.Unauthenticated, Message = "Authentication is required to submit a review."
            });
        }

        var booking = await _reviewsStore.FindBookingForReviewAsync(bookingId, cancellationToken);
        if (booking is null)
        {
            return ServiceResult<ReviewContext>.Failure(new ServiceError
            {
                Code = ErrorCodes.BookingNotFound, Message = "Booking was not found."
            });
        }

        if (booking.Status != BookingStatus.Completed)
        {
            return ServiceResult<ReviewContext>.Failure(new ServiceError
            {
                Code = ErrorCodes.NotCompleted, Message = "Reviews can only be submitted for completed bookings."
            });
        }

        return ServiceResult<ReviewContext>.Success(new ReviewContext(callerId, booking));
    }

    private async Task<ServiceResult<BookingReviewStatusResponse>> BuildStatusAsync(
        Guid callerId, Booking booking, CancellationToken cancellationToken)
    {
        var isRenter = callerId == booking.RenterId;
        var isOwner = callerId == booking.Listing.OwnerId;
        var completed = booking.Status == BookingStatus.Completed;

        var hasToy = await _reviewsStore.HasToyReviewAsync(booking.Id, cancellationToken);
        var hasOwner = await _reviewsStore.HasOwnerReviewAsync(booking.Id, cancellationToken);
        var hasRenter = await _reviewsStore.HasRenterReviewAsync(booking.Id, cancellationToken);

        return ServiceResult<BookingReviewStatusResponse>.Success(new BookingReviewStatusResponse
        {
            BookingId = booking.Id,
            Role = isRenter ? "renter" : isOwner ? "owner" : "none",
            IsCompleted = completed,
            HasToyReview = hasToy,
            HasOwnerReview = hasOwner,
            HasRenterReview = hasRenter,
            CanReviewToy = completed && isRenter && !hasToy,
            CanReviewOwner = completed && isRenter && !hasOwner,
            CanReviewRenter = completed && isOwner && !hasRenter
        });
    }

    private static ReviewCommentResponse MapComment(
        Guid id, User reviewer, Booking booking, string comment, DateTime createdAt) => new()
    {
        Id = id,
        ReviewerFirstName = reviewer.FirstName,
        ReviewerLastName = reviewer.LastName,
        ReviewerAvatarUrl = reviewer.AvatarUrl,
        Comment = comment,
        RentedDays = booking.EndDate.DayNumber - booking.StartDate.DayNumber + 1,
        CreatedAt = createdAt
    };

    private static double Avg<T>(IReadOnlyCollection<T> items, Func<T, double> selector) =>
        items.Count == 0 ? 0.0 : Math.Round(items.Average(selector), 1);

    private static IReadOnlyList<int> Distribution(IEnumerable<int> ratings)
    {
        var buckets = new int[5];
        foreach (var rating in ratings)
        {
            if (rating is >= 1 and <= 5)
            {
                buckets[rating - 1]++;
            }
        }
        return buckets;
    }

    private static int RoundToBucket(double value) =>
        Math.Clamp((int)Math.Round(value, MidpointRounding.AwayFromZero), 1, 5);

    private static string? NormalizeComment(string? comment) =>
        string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();

    private static ServiceResult<BookingReviewStatusResponse> Failure(string code, string message) =>
        ServiceResult<BookingReviewStatusResponse>.Failure(new ServiceError { Code = code, Message = message });
}
