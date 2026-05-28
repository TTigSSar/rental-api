using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.Common;
using RentalPlatform.Application.DTOs;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Application.Services;

public sealed class ReviewsService : IReviewsService
{
    private static class ErrorCodes
    {
        public const string Unauthenticated  = "review.unauthenticated";
        public const string BookingNotFound  = "review.booking_not_found";
        public const string NotCompleted     = "review.booking_not_completed";
        public const string Forbidden        = "review.forbidden";
        public const string AlreadySubmitted = "review.already_submitted";
        public const string InvalidRating    = "review.invalid_rating";
    }

    private readonly ICurrentUserContext _currentUserContext;
    private readonly IReviewsStore _reviewsStore;

    public ReviewsService(ICurrentUserContext currentUserContext, IReviewsStore reviewsStore)
    {
        _currentUserContext = currentUserContext;
        _reviewsStore = reviewsStore;
    }

    public async Task<ServiceResult<ReviewResponse>> CreateAsync(
        CreateReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        if (_currentUserContext.UserId is not { } callerId)
        {
            return Failure<ReviewResponse>(ErrorCodes.Unauthenticated, "Authentication is required to submit a review.");
        }

        if (request.Rating < 1 || request.Rating > 5)
        {
            return Failure<ReviewResponse>(ErrorCodes.InvalidRating, "Rating must be between 1 and 5.");
        }

        var booking = await _reviewsStore.FindBookingForReviewAsync(request.BookingId, cancellationToken);
        if (booking is null)
        {
            return Failure<ReviewResponse>(ErrorCodes.BookingNotFound, "Booking was not found.");
        }

        if (booking.Status != BookingStatus.Completed)
        {
            return Failure<ReviewResponse>(ErrorCodes.NotCompleted, "Reviews can only be submitted for completed bookings.");
        }

        // Determine which side of the booking the caller represents.
        ReviewerRole role;
        Guid revieweeId;

        if (callerId == booking.RenterId)
        {
            role = ReviewerRole.Renter;
            revieweeId = booking.Listing.OwnerId;
        }
        else if (callerId == booking.Listing.OwnerId)
        {
            role = ReviewerRole.Owner;
            revieweeId = booking.RenterId;
        }
        else
        {
            return Failure<ReviewResponse>(ErrorCodes.Forbidden, "Only the renter or owner of this booking can submit a review.");
        }

        var alreadyReviewed = await _reviewsStore.HasReviewForBookingAsync(
            request.BookingId, role, cancellationToken);

        if (alreadyReviewed)
        {
            return Failure<ReviewResponse>(ErrorCodes.AlreadySubmitted, "You have already submitted a review for this booking.");
        }

        var reviewer = await _reviewsStore.FindUserByIdAsync(callerId, cancellationToken);

        var review = new Review
        {
            Id = Guid.NewGuid(),
            BookingId = request.BookingId,
            ListingId = booking.ListingId,
            ReviewerId = callerId,
            RevieweeId = revieweeId,
            ReviewerRole = role,
            Rating = request.Rating,
            Comment = string.IsNullOrWhiteSpace(request.Comment) ? null : request.Comment.Trim(),
            CreatedAt = DateTime.UtcNow
        };

        await _reviewsStore.AddAsync(review, cancellationToken);

        return ServiceResult<ReviewResponse>.Success(MapReview(review, reviewer));
    }

    public async Task<ServiceResult<IReadOnlyCollection<ReviewResponse>>> GetByListingAsync(
        Guid listingId,
        CancellationToken cancellationToken = default)
    {
        var reviews = await _reviewsStore.GetByListingAsync(listingId, cancellationToken);
        var response = reviews.Select(r => MapReview(r, r.Reviewer)).ToList();
        return ServiceResult<IReadOnlyCollection<ReviewResponse>>.Success(response);
    }

    public async Task<ServiceResult<IReadOnlyCollection<ReviewResponse>>> GetByUserAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var reviews = await _reviewsStore.GetByUserAsync(userId, cancellationToken);
        var response = reviews.Select(r => MapReview(r, r.Reviewer)).ToList();
        return ServiceResult<IReadOnlyCollection<ReviewResponse>>.Success(response);
    }

    private static ReviewResponse MapReview(Review review, User? reviewer) => new()
    {
        Id = review.Id,
        BookingId = review.BookingId,
        ListingId = review.ListingId,
        ReviewerId = review.ReviewerId,
        RevieweeId = review.RevieweeId,
        ReviewerRole = review.ReviewerRole,
        Rating = review.Rating,
        Comment = review.Comment,
        CreatedAt = review.CreatedAt,
        ReviewerFirstName = reviewer?.FirstName ?? string.Empty,
        ReviewerLastName = reviewer?.LastName ?? string.Empty,
        ReviewerAvatarUrl = reviewer?.AvatarUrl
    };

    private static ServiceResult<T> Failure<T>(string code, string message) =>
        ServiceResult<T>.Failure(new ServiceError { Code = code, Message = message });
}
