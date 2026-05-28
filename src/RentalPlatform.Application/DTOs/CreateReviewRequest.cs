namespace RentalPlatform.Application.DTOs;

public sealed class CreateReviewRequest
{
    public Guid BookingId { get; init; }
    public int Rating { get; init; }
    public string? Comment { get; init; }
}
