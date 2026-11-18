using System.Net;
using System.Security.Cryptography;
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

            // A p256dh of the right length that is not a point on P-256 can never be encrypted to: the row is
            // permanently broken, so it is reported Gone and the dispatcher deletes it (ADR-067).
            if (!IsValidP256Point(delivery.P256dh))
            {
                _logger.LogWarning("Push subscription has an invalid p256dh key; it will be removed.");
                return PushSendOutcome.Gone;
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
            // 3xx is not followed (redirects are disabled on the client) and lands here as a failure; row kept.
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

    private static bool IsValidP256Point(string p256dh)
    {
        try
        {
            var padded = p256dh.Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
            var bytes = Convert.FromBase64String(padded);
            if (bytes.Length != 65 || bytes[0] != 0x04)
            {
                return false;
            }

            using var ecdh = ECDiffieHellman.Create();
            ecdh.ImportParameters(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = bytes[1..33], Y = bytes[33..65] }
            });
            return true;
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException or ArgumentException)
        {
            return false;
        }
    }
}
