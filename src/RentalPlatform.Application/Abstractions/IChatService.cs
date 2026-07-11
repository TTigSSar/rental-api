using RentalPlatform.Application.Common;
using RentalPlatform.Application.DTOs;

namespace RentalPlatform.Application.Abstractions;

public interface IChatService
{
    Task<ServiceResult<IReadOnlyCollection<ChatConversationResponse>>> GetConversationsAsync(
        CancellationToken cancellationToken = default);

    Task<ServiceResult<ChatConversationDetailsResponse>> GetConversationAsync(
        Guid conversationId,
        int? page,
        int? pageSize,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Booking → thread entry point: returns the conversation for a booking, creating it on first
    /// access. The caller must be the booking's owner or renter.
    /// </summary>
    Task<ServiceResult<ChatConversationDetailsResponse>> GetOrCreateForBookingAsync(
        Guid bookingId,
        CancellationToken cancellationToken = default);

    Task<ServiceResult<ChatMessageResponse>> SendMessageAsync(
        SendChatMessageRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Sends an image message (with an optional caption) into a conversation.</summary>
    Task<ServiceResult<ChatMessageResponse>> SendImageMessageAsync(
        SendChatImageMessageRequest request,
        CancellationToken cancellationToken = default);

    Task<ServiceResult<bool>> MarkReadAsync(Guid conversationId, CancellationToken cancellationToken = default);
}
