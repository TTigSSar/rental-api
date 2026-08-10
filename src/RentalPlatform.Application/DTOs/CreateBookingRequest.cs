using System.ComponentModel.DataAnnotations;

namespace RentalPlatform.Application.DTOs;

public sealed class CreateBookingRequest
{
    [Required]
    public Guid ListingId { get; init; }

    [Required]
    public DateOnly StartDate { get; init; }

    [Required]
    public DateOnly EndDate { get; init; }

    // Optional free-text note from the renter to the owner. Trimmed and, if longer than 280
    // characters after trimming, rejected by BookingsService (booking.note_too_long) — the
    // service handles this rather than a [MaxLength] attribute because trimming must happen
    // before the length is judged (see BookingsService.CreateAsync).
    public string? Note { get; init; }
}
