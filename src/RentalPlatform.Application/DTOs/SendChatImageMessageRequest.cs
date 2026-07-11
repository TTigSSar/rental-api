namespace RentalPlatform.Application.DTOs;

/// <summary>Service-layer request for POST /api/chat/conversations/{id}/messages/image.</summary>
public sealed class SendChatImageMessageRequest
{
    public required Guid ConversationId { get; init; }
    public required string FileName { get; init; }
    public required string ContentType { get; init; }
    public required long Length { get; init; }
    public required Stream Content { get; init; }

    /// <summary>Optional caption accompanying the image.</summary>
    public string? Caption { get; init; }
}
