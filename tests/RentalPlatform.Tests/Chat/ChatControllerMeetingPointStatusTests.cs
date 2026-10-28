using Microsoft.AspNetCore.Mvc;
using RentalPlatform.Api.Controllers;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.Common;
using RentalPlatform.Application.DTOs;
using Xunit;

namespace RentalPlatform.Tests.Chat;

// ChatController.FromError status mapping for the meeting-point error codes (ADR-053).
// Controller unit test over a stubbed IChatService that returns each failure.
public sealed class ChatControllerMeetingPointStatusTests
{
    [Theory]
    [InlineData("chat.meeting_point_owner_only", 403)]
    [InlineData("chat.meeting_point_unavailable", 409)]
    [InlineData("chat.home_point_required", 409)]
    [InlineData("chat.meeting_point_invalid", 400)]
    [InlineData("chat.meeting_point_outside_yerevan", 400)]
    public async Task ShareMeetingPoint_Maps_Error_Code_To_Http_Status(string code, int expectedStatus)
    {
        var controller = new ChatController(new FailingChatService(code));

        var result = await controller.ShareMeetingPoint(
            Guid.NewGuid(), new ShareMeetingPointRequest { Kind = "home" }, CancellationToken.None);

        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(expectedStatus, objectResult.StatusCode);
        var problem = Assert.IsAssignableFrom<ProblemDetails>(objectResult.Value);
        Assert.Equal(expectedStatus, problem.Status);
    }

    private sealed class FailingChatService(string code) : IChatService
    {
        public Task<ServiceResult<ChatMessageResponse>> ShareMeetingPointAsync(
            Guid conversationId, ShareMeetingPointRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(ServiceResult<ChatMessageResponse>.Failure(new ServiceError { Code = code, Message = code }));

        public Task<ServiceResult<IReadOnlyCollection<ChatConversationResponse>>> GetConversationsAsync(
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ServiceResult<ChatConversationDetailsResponse>> GetConversationAsync(
            Guid conversationId, int? page, int? pageSize, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ServiceResult<ChatConversationDetailsResponse>> GetOrCreateForBookingAsync(
            Guid bookingId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ServiceResult<ChatMessageResponse>> SendMessageAsync(
            SendChatMessageRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ServiceResult<ChatMessageResponse>> SendImageMessageAsync(
            SendChatImageMessageRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ServiceResult<bool>> MarkReadAsync(
            Guid conversationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
