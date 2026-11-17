using System.Security.Cryptography;
using System.Text;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.Common;
using RentalPlatform.Application.DTOs;

namespace RentalPlatform.Application.Services;

// ADR-067 sections 7 and 8.
public sealed class PushSubscriptionService : IPushSubscriptionService
{
    public const int MaxSubscriptionsPerUser = 10;

    private static class ErrorCodes
    {
        public const string Unauthenticated = "push.unauthenticated";
        public const string UserBlocked = "push.user_blocked";
        public const string NotConfigured = "push.not_configured";
        public const string InvalidSubscription = "push.invalid_subscription";
    }

    private readonly ICurrentUserContext _currentUserContext;
    private readonly IPushSubscriptionStore _store;
    private readonly IWebPushSettings _settings;
    private readonly TimeProvider _timeProvider;

    public PushSubscriptionService(
        ICurrentUserContext currentUserContext,
        IPushSubscriptionStore store,
        IWebPushSettings settings,
        TimeProvider timeProvider)
    {
        _currentUserContext = currentUserContext;
        _store = store;
        _settings = settings;
        _timeProvider = timeProvider;
    }

    public ServiceResult<PushPublicKeyResponse> GetPublicKey() =>
        _settings.IsEnabled && !string.IsNullOrWhiteSpace(_settings.PublicKey)
            ? ServiceResult<PushPublicKeyResponse>.Success(new PushPublicKeyResponse { PublicKey = _settings.PublicKey })
            : Failure<PushPublicKeyResponse>(ErrorCodes.NotConfigured, "Web Push is not configured.");

    public async Task<ServiceResult<bool>> SubscribeAsync(
        PushSubscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (_currentUserContext.UserId is not { } userId)
        {
            return Failure<bool>(ErrorCodes.Unauthenticated, "Current user is not authenticated.");
        }

        var user = await _store.FindUserByIdAsync(userId, cancellationToken);
        if (user is null || user.DeletedAt is not null)
        {
            return Failure<bool>(ErrorCodes.Unauthenticated, "Current user is not authenticated.");
        }

        if (user.IsBlocked)
        {
            return Failure<bool>(ErrorCodes.UserBlocked, "Blocked users cannot manage push subscriptions.");
        }

        if (!PushEndpointValidator.IsValidSubscription(request.Endpoint, request.P256dh, request.Auth))
        {
            return Failure<bool>(ErrorCodes.InvalidSubscription, "The push subscription is not valid.");
        }

        await _store.UpsertAsync(
            userId,
            request.Endpoint!,
            HashEndpoint(request.Endpoint!),
            request.P256dh!,
            request.Auth!,
            _timeProvider.GetUtcNow().UtcDateTime,
            MaxSubscriptionsPerUser,
            cancellationToken);

        return ServiceResult<bool>.Success(true);
    }

    // Blocked users may still remove a subscription: it only ever reduces what the server holds.
    public async Task<ServiceResult<bool>> UnsubscribeAsync(
        PushUnsubscribeRequest request,
        CancellationToken cancellationToken = default)
    {
        if (_currentUserContext.UserId is not { } userId)
        {
            return Failure<bool>(ErrorCodes.Unauthenticated, "Current user is not authenticated.");
        }

        if (string.IsNullOrEmpty(request.Endpoint) || request.Endpoint.Length > PushEndpointValidator.MaxEndpointLength)
        {
            return Failure<bool>(ErrorCodes.InvalidSubscription, "The push subscription is not valid.");
        }

        var removed = await _store.DeleteAsync(userId, HashEndpoint(request.Endpoint), cancellationToken);
        return ServiceResult<bool>.Success(removed);
    }

    public static byte[] HashEndpoint(string endpoint) => SHA256.HashData(Encoding.UTF8.GetBytes(endpoint));

    private static ServiceResult<T> Failure<T>(string code, string message) =>
        ServiceResult<T>.Failure(new ServiceError { Code = code, Message = message });
}
