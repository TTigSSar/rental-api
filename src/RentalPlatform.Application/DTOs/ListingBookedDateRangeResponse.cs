namespace RentalPlatform.Application.DTOs;

public sealed class ListingBookedDateRangeResponse
{
    public DateOnly StartDate { get; init; }
    public DateOnly EndDate { get; init; }
}
