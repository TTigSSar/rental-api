using RentalPlatform.Application.Common;
using Xunit;

namespace RentalPlatform.Tests.Push;

// ADR-067 section 8: the SSRF guard in front of every stored and every used push endpoint.
public sealed class PushEndpointValidatorTests
{
    [Theory]
    [InlineData("https://fcm.googleapis.com/fcm/send/abc")]
    [InlineData("https://updates.push.services.mozilla.com/wpush/v2/abc")]
    [InlineData("https://wns2-par02p.notify.windows.com/w/?token=abc")]
    [InlineData("https://web.push.apple.com/abc")]
    [InlineData("https://fcm.googleapis.com:443/fcm/send/abc")]
    public void An_Allow_Listed_Https_Endpoint_Is_Accepted(string endpoint) =>
        Assert.True(PushEndpointValidator.IsValidEndpoint(endpoint));

    [Theory]
    [InlineData("http://fcm.googleapis.com/fcm/send/abc")]                       // not https
    [InlineData("https://evil.com/fcm.googleapis.com")]                           // allow-listed name only in the path
    [InlineData("https://fcm.googleapis.com.evil.com/fcm/send/abc")]              // allow-listed name as a prefix
    [InlineData("https://evilfcm.googleapis.com/fcm/send/abc")]                   // not a dot boundary
    [InlineData("https://evilpush.apple.com/abc")]                                // not a dot boundary
    [InlineData("https://push.apple.com/abc")]                                    // the bare suffix is not a subdomain
    [InlineData("https://notify.windows.com.evil.com/abc")]
    [InlineData("https://127.0.0.1/abc")]                                         // IP literal
    [InlineData("https://[::1]/abc")]                                             // IPv6 literal
    [InlineData("https://user:pass@fcm.googleapis.com/fcm/send/abc")]             // userinfo
    [InlineData("https://fcm.googleapis.com@evil.com/abc")]                       // userinfo trick
    [InlineData("https://fcm.googleapis.com:8443/fcm/send/abc")]                  // non-default port
    [InlineData("https://fcm.googleapis.com./fcm/send/abc")]                      // trailing dot
    [InlineData("/fcm/send/abc")]                                                 // relative
    [InlineData("ftp://fcm.googleapis.com/abc")]
    [InlineData("https://fcm.googleapis.com/a b")]                                // whitespace
    [InlineData("")]
    [InlineData(null)]
    public void Anything_Else_Is_Refused(string? endpoint) =>
        Assert.False(PushEndpointValidator.IsValidEndpoint(endpoint));

    [Fact]
    public void An_Endpoint_Over_2048_Characters_Is_Refused()
    {
        var atLimit = "https://fcm.googleapis.com/" + new string('a', 2048 - "https://fcm.googleapis.com/".Length);
        Assert.Equal(2048, atLimit.Length);
        Assert.True(PushEndpointValidator.IsValidEndpoint(atLimit));
        Assert.False(PushEndpointValidator.IsValidEndpoint(atLimit + "a"));
    }

    [Fact]
    public void Keys_Of_The_Right_Length_Are_Accepted()
    {
        Assert.True(PushEndpointValidator.IsValidSubscription(
            PushTestKeys.FcmEndpoint(), PushTestKeys.P256dh(), PushTestKeys.Auth()));
    }

    [Theory]
    [InlineData(64, 16)]
    [InlineData(66, 16)]
    [InlineData(65, 15)]
    [InlineData(65, 17)]
    [InlineData(0, 16)]
    [InlineData(65, 0)]
    public void Keys_Of_The_Wrong_Length_Are_Refused(int p256dhBytes, int authBytes)
    {
        var p256dh = p256dhBytes == 0 ? "" : PushTestKeys.Base64Url(p256dhBytes);
        var auth = authBytes == 0 ? "" : PushTestKeys.Base64Url(authBytes);
        Assert.False(PushEndpointValidator.IsValidSubscription(PushTestKeys.FcmEndpoint(), p256dh, auth));
    }

    [Fact]
    public void Keys_That_Are_Not_Base64Url_Are_Refused()
    {
        // Standard base64 characters '+' and '/' are not base64url.
        var standard = Convert.ToBase64String(Enumerable.Repeat((byte)0xFB, 65).ToArray());
        Assert.Contains('+', standard);
        Assert.False(PushEndpointValidator.IsValidSubscription(PushTestKeys.FcmEndpoint(), standard, PushTestKeys.Auth()));
        Assert.False(PushEndpointValidator.IsValidSubscription(PushTestKeys.FcmEndpoint(), PushTestKeys.P256dh(), "not base64url!!!!!!!!"));
        Assert.False(PushEndpointValidator.IsValidSubscription(PushTestKeys.FcmEndpoint(), null, null));
    }
}
