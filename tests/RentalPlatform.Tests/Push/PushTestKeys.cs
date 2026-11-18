namespace RentalPlatform.Tests.Push;

internal static class PushTestKeys
{
    public static string Base64Url(int length)
    {
        var bytes = new byte[length];
        Random.Shared.NextBytes(bytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static string P256dh() => Base64Url(65);
    public static string Auth() => Base64Url(16);

    public static string FcmEndpoint() => $"https://fcm.googleapis.com/fcm/send/{Guid.NewGuid():N}";
}
