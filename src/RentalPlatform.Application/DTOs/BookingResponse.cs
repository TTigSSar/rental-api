using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Application.DTOs;

public sealed class BookingResponse
{
    public Guid Id { get; init; }
    public Guid ListingId { get; init; }
    public string ListingTitle { get; init; } = string.Empty;
    public string? ListingPrimaryImageUrl { get; init; }
    public string Currency { get; init; } = string.Empty;
    public decimal PricePerDay { get; init; }
    public decimal? DepositAmount { get; init; }
    public DateOnly StartDate { get; init; }
    public DateOnly EndDate { get; init; }
    public decimal TotalPrice { get; init; }
    public string OwnerFirstName { get; init; } = string.Empty;
    public string OwnerLastName { get; init; } = string.Empty;
    public BookingStatus Status { get; init; }
    public DateTime ExpiresAt { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
}
