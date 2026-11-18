using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Infrastructure.Services;
using Xunit;

namespace RentalPlatform.Tests.Push;

public class WebPushSenderTests
{
    [Fact]
    public async Task InvalidP256Point_IsGone_AndNothingIsSent()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Created));
        var sender = CreateSender(handler);

        // 65 random bytes with a valid length but (almost surely) not a point on P-256.
        var outcome = await sender.SendAsync(Delivery(PushTestKeys.P256dh()));

        Assert.Equal(PushSendOutcome.Gone, outcome);
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task Redirect_IsFailed_AndNotFollowed()
    {
        var handler = new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
            response.Headers.Location = new Uri("https://evil.example/steal");
            return response;
        });
        var sender = CreateSender(handler);

        var outcome = await sender.SendAsync(Delivery(ValidP256dh()));

        Assert.Equal(PushSendOutcome.Failed, outcome);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task Timeout_IsFailed()
    {
        var handler = new StubHandler(_ => throw new TaskCanceledException("timeout", new TimeoutException()));
        var sender = CreateSender(handler);

        var outcome = await sender.SendAsync(Delivery(ValidP256dh()));

        Assert.Equal(PushSendOutcome.Failed, outcome);
    }

    [Fact]
    public async Task NotFound_IsGone_AndCreated_IsSent()
    {
        Assert.Equal(PushSendOutcome.Gone,
            await CreateSender(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Gone))).SendAsync(Delivery(ValidP256dh())));
        Assert.Equal(PushSendOutcome.Sent,
            await CreateSender(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Created))).SendAsync(Delivery(ValidP256dh())));
    }

    private static PushDelivery Delivery(string p256dh) =>
        new(PushTestKeys.FcmEndpoint(), p256dh, PushTestKeys.Auth(), "{}", Guid.NewGuid().ToString("N"), 60);

    private static string ValidP256dh()
    {
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var q = ecdh.ExportParameters(false).Q;
        var bytes = new byte[65];
        bytes[0] = 0x04;
        q.X!.CopyTo(bytes, 1);
        q.Y!.CopyTo(bytes, 33);
        return Base64Url(bytes);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static WebPushSender CreateSender(StubHandler handler)
    {
        using var vapid = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var p = vapid.ExportParameters(true);
        var publicKey = new byte[65];
        publicKey[0] = 0x04;
        p.Q.X!.CopyTo(publicKey, 1);
        p.Q.Y!.CopyTo(publicKey, 33);
        var options = Options.Create(new WebPushOptions
        {
            Vapid = new VapidOptions
            {
                PublicKey = Base64Url(publicKey),
                PrivateKey = Base64Url(p.D!),
                Subject = "mailto:test@example.com"
            }
        });
        return new WebPushSender(new HttpClient(handler), new WebPushSettings(options), NullLogger<WebPushSender>.Instance);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(respond(request));
        }
    }
}
