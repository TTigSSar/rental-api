using System.Net;
using Lib.Net.Http.WebPush;
using Lib.Net.Http.WebPush.Authentication;
using Microsoft.Extensions.Logging;
using RentalPlatform.Application.Abstractions;

namespace RentalPlatform.Infrastructure.Services;

// The real IPushSender (ADR-067): Lib.Net.Http.WebPush, VAPID. Never throws. Delivery is not retried.
public sealed class WebPushSender : IPushSender
{
    private readonly HttpClient _httpClient;
    private readonly WebPushSettings _settings;
    private readonly ILogger<WebPushSender> _logger;

    public WebPushSender(HttpClient httpClient, WebPushSettings settings, ILogger<WebPushSender> logger)
    {
        _httpClient = httpClient;
        _settings = settings;
        _logger = logger;
    }

    public async Task<PushSendOutcome> SendAsync(PushDelivery delivery, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!_settings.IsEnabled)
            {
                return PushSendOutcome.Failed;
            }

            var client = new PushServiceClient(_httpClient)
            {
                // A 429 is not retried here (ADR-067 section 5): no waiting inside the single consumer.
                AutoRetryAfter = false,
                DefaultAuthentication = new VapidAuthentication(_settings.PublicKey!, _settings.PrivateKey!)
                {
                    Subject = _settings.Subject
                }
            };

            var subscription = new Lib.Net.Http.WebPush.PushSubscription { Endpoint = delivery.Endpoint };
            subscription.SetKey(PushEncryptionKeyName.P256DH, delivery.P256dh);
            subscription.SetKey(PushEncryptionKeyName.Auth, delivery.Auth);

            var message = new PushMessage(delivery.PayloadJson)
            {
                Topic = delivery.Topic,
                TimeToLive = delivery.TimeToLiveSeconds,
                Urgency = PushMessageUrgency.High
            };

            await client.RequestPushMessageDeliveryAsync(subscription, message, cancellationToken);
            return PushSendOutcome.Sent;
        }
        catch (PushServiceClientException exception)
        {
            if (exception.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                return PushSendOutcome.Gone;
            }

            // Status only: the endpoint is a capability URL and stays out of the log.
            _logger.LogWarning("Push service answered {StatusCode}; the push was not delivered.", (int)exception.StatusCode);
            return PushSendOutcome.Failed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning("Push delivery failed ({ExceptionType}).", exception.GetType().Name);
            return PushSendOutcome.Failed;
        }
    }
}
