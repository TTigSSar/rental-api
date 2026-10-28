namespace RentalPlatform.Application.DTOs;

/// <summary>
/// Body for POST /api/chat/conversations/{conversationId}/meeting-point. Kind is "home" | "other".
/// For "home" the coordinates are ignored (the server reads the owner's home point); for "other"
/// both are required and must fall inside a Yerevan district.
/// </summary>
public sealed class ShareMeetingPointRequest
{
    public string Kind { get; init; } = string.Empty;
    public decimal? Latitude { get; init; }
    public decimal? Longitude { get; init; }
}
