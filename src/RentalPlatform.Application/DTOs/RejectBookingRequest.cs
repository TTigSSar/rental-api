using System.ComponentModel.DataAnnotations;

namespace RentalPlatform.Application.DTOs;

public sealed class RejectBookingRequest
{
    /// <summary>
    /// Why the owner rejected the request. Either a known reason code
    /// (e.g. "dates_unavailable") or free text for "other". Optional.
    /// </summary>
    [MaxLength(500)]
    public string? Reason { get; init; }
}
