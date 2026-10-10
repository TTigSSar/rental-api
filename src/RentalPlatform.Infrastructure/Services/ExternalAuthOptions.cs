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

    /// <summary>
    /// The audiences that are real client ids: trimmed, with empty entries and placeholder-looking
    /// ones ("set-...", "your-...") dropped. compose maps an unset variable to "" (M-016), and a
    /// template value must never count as configuration - it would turn "not configured" (503) into
    /// a confusing "invalid token" (400) and silence the startup warning.
    /// </summary>
    public string[] ConfiguredAudiences =>
        (ValidAudiences ?? Array.Empty<string>())
            .Where(static audience => !string.IsNullOrWhiteSpace(audience))
            .Select(static audience => audience.Trim())
            .Where(static audience =>
                !audience.StartsWith("set-", StringComparison.OrdinalIgnoreCase) &&
                !audience.StartsWith("your-", StringComparison.OrdinalIgnoreCase))
            .ToArray();

    public bool IsConfigured => ConfiguredAudiences.Length > 0;
}

public sealed class AppleExternalAuthOptions
{
    public string Issuer { get; init; } = "https://appleid.apple.com";
    public string JwksUrl { get; init; } = "https://appleid.apple.com/auth/keys";
    public string[] ValidAudiences { get; init; } = Array.Empty<string>();
}
