using System.ComponentModel.DataAnnotations;

namespace RentalPlatform.Application.DTOs;

/// <summary>Body for POST /api/chat/messages. Matches the frontend payload { conversationId, content }.</summary>
public sealed class SendChatMessageRequest
{
    [Required]
    public Guid ConversationId { get; init; }

    [Required]
    [MaxLength(4000)]
    public string Content { get; init; } = string.Empty;
}
