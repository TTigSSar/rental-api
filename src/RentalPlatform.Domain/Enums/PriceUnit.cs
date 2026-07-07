namespace RentalPlatform.Domain.Enums;

// The rental period a listing's price applies to. The numeric price column (PricePerDay)
// holds the amount; this records which period that amount covers. Daily is the default and
// matches today's booking-cost math (see BookingsService) — other units will affect totals later.
public enum PriceUnit
{
    Hourly = 0,
    Daily = 1,
    Weekly = 2,
    Monthly = 3,
    Yearly = 4
}
