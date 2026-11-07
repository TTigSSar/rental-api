using RentalPlatform.Application.Common;
using RentalPlatform.Application.DTOs;

namespace RentalPlatform.Application.Abstractions;

public interface IChatEmailUnsubscribeService
{
    /// <summary>
    /// Turns chat emails off for the token's user. Failure code <c>chat_email.unsubscribe_invalid</c> for an
    /// invalid, expired or altered token and for an unknown or deleted user.
    /// </summary>
    Task<ServiceResult<ChatEmailUnsubscribeResponse>> UnsubscribeAsync(string? token, CancellationToken cancellationToken = default);
}
