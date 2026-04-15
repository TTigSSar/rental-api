namespace RentalPlatform.Infrastructure.Services;

public sealed class ExternalAuthOptions
{
    public const string SectionName = "ExternalAuth";

    public GoogleExternalAuthOptions Google { get; init; } = new();
    public AppleExternalAuthOptions Apple { get; init; } = new();
}

public sealed class GoogleExternalAuthOptions
{
    public string[] ValidAudiences { get; init; } = Array.Empty<string>();
}

public sealed class AppleExternalAuthOptions
{
    public string Issuer { get; init; } = "https://appleid.apple.com";
    public string JwksUrl { get; init; } = "https://appleid.apple.com/auth/keys";
    public string[] ValidAudiences { get; init; } = Array.Empty<string>();
}
