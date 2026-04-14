using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Application.DTOs;

public sealed class BookingRequestResponse
{
    public Guid Id { get; init; }
    public Guid ListingId { get; init; }
    public string ListingTitle { get; init; } = string.Empty;
    public Guid RenterId { get; init; }
    public string RenterEmail { get; init; } = string.Empty;
    public string RenterFirstName { get; init; } = string.Empty;
    public string RenterLastName { get; init; } = string.Empty;
    public DateOnly StartDate { get; init; }
    public DateOnly EndDate { get; init; }
    public decimal TotalPrice { get; init; }
    public BookingStatus Status { get; init; }
    public DateTime ExpiresAt { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
}
