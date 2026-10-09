using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RentalPlatform.Application.Abstractions;

namespace RentalPlatform.Infrastructure.Services;

// Resend over plain HttpClient, no SDK (ADR-029). Contract:
//  - never throws: a failed send is logged at Error and the user recovers through resend;
//  - a 10 s timeout on this class's OWN token, deliberately not the request's: the row is already
//    committed, and a client that hung up must not also cost the user their email;
//  - no retry and no Idempotency-Key (nothing to deduplicate without retries);
//  - logs the provider status only - never the body, the link token or the API key (M-013).
public sealed class ResendEmailSender : IEmailSender
{
    public static readonly Uri Endpoint = new("https://api.resend.com/emails");
    public static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(10);

    private readonly HttpClient _httpClient;
    private readonly EmailOptions _options;
    private readonly ILogger<ResendEmailSender> _logger;
    private readonly TimeSpan _timeout;

    // sendTimeout exists so a test can prove the timeout path without waiting ten seconds; DI leaves
    // it at its default.
    public ResendEmailSender(
        HttpClient httpClient,
        IOptions<EmailOptions> options,
        ILogger<ResendEmailSender> logger,
        TimeSpan? sendTimeout = null)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
        _timeout = sendTimeout ?? SendTimeout;
    }

    // cancellationToken is intentionally not used for cancellation; see the class remarks.
    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        if (!_options.HasApiKey)
        {
            _logger.LogError("Resend send skipped: Email:Resend:ApiKey is not configured.");
            return;
        }

        using var timeout = new CancellationTokenSource(_timeout);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            {
                Content = JsonContent.Create(new ResendPayload
                {
                    From = _options.EffectiveFrom,
                    To = new[] { message.To },
                    Subject = message.Subject,
                    Html = message.HtmlBody,
                    Text = message.TextBody
                })
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.Resend.ApiKey!.Trim());

            using var response = await _httpClient.SendAsync(request, timeout.Token);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Resend accepted an email (HTTP {StatusCode}).", (int)response.StatusCode);
            }
            else
            {
                _logger.LogError("Resend rejected an email (HTTP {StatusCode}).", (int)response.StatusCode);
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            _logger.LogError("Resend send timed out after {Seconds} s.", _timeout.TotalSeconds);
        }
        catch (Exception exception)
        {
            // HttpRequestException text never carries the request body or the Authorization header.
            _logger.LogError(exception, "Resend send failed ({ExceptionType}).", exception.GetType().Name);
        }
    }

    private sealed class ResendPayload
    {
        [JsonPropertyName("from")] public string From { get; init; } = string.Empty;
        [JsonPropertyName("to")] public string[] To { get; init; } = Array.Empty<string>();
        [JsonPropertyName("subject")] public string Subject { get; init; } = string.Empty;
        [JsonPropertyName("html")] public string Html { get; init; } = string.Empty;
        [JsonPropertyName("text")] public string Text { get; init; } = string.Empty;
    }
}
