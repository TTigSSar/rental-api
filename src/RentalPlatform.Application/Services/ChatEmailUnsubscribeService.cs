using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.Common;
using RentalPlatform.Application.DTOs;

namespace RentalPlatform.Application.Services;

public sealed class ChatEmailUnsubscribeService : IChatEmailUnsubscribeService
{
    public const string InvalidCode = "chat_email.unsubscribe_invalid";

    private readonly IChatEmailUnsubscribeTokens _tokens;
    private readonly IUserAuthStore _userAuthStore;

    public ChatEmailUnsubscribeService(IChatEmailUnsubscribeTokens tokens, IUserAuthStore userAuthStore)
    {
        _tokens = tokens;
        _userAuthStore = userAuthStore;
    }

    public async Task<ServiceResult<ChatEmailUnsubscribeResponse>> UnsubscribeAsync(
        string? token, CancellationToken cancellationToken = default)
    {
        var userId = _tokens.Validate(token);
        var user = userId is { } id ? await _userAuthStore.FindByIdAsync(id, cancellationToken) : null;
        if (user is null || user.DeletedAt is not null)
        {
            return ServiceResult<ChatEmailUnsubscribeResponse>.Failure(new ServiceError
            {
                Code = InvalidCode,
                Message = "This link no longer works."
            });
        }

        if (!user.ChatEmailsEnabled)
        {
            return ServiceResult<ChatEmailUnsubscribeResponse>.Success(
                new ChatEmailUnsubscribeResponse { Result = ChatEmailUnsubscribeResponse.AlreadyOff });
        }

        user.ChatEmailsEnabled = false;
        await _userAuthStore.SaveChangesAsync(cancellationToken);

        return ServiceResult<ChatEmailUnsubscribeResponse>.Success(
            new ChatEmailUnsubscribeResponse { Result = ChatEmailUnsubscribeResponse.Unsubscribed });
    }
}
