using RentalPlatform.Application.Abstractions;

namespace RentalPlatform.Infrastructure.Services;

// Bound from "WebPush" (ADR-067 section 14). Plain strings on purpose (M-016): compose maps an unset
// variable to "", and anything else than a string binder would throw on "" at startup.
public sealed class WebPushOptions
{
    public const string SectionName = "WebPush";

    public VapidOptions Vapid { get; init; } = new();
}

public sealed class VapidOptions
{
    public string? PublicKey { get; init; }
    public string? PrivateKey { get; init; }

    /// <summary>The VAPID contact: a mailto: or https: URL.</summary>
    public string? Subject { get; init; }
}

// Singleton view of the options for the application layer and the sender. Push is on only when all three
// values are present; otherwise the dispatcher no-ops and GET /api/push/public-key answers 404.
public sealed class WebPushSettings : IWebPushSettings
{
    private readonly VapidOptions _vapid;

    public WebPushSettings(Microsoft.Extensions.Options.IOptions<WebPushOptions> options)
    {
        _vapid = options.Value.Vapid;
    }

    public string? PublicKey => Clean(_vapid.PublicKey);
    public string? PrivateKey => Clean(_vapid.PrivateKey);
    public string? Subject => Clean(_vapid.Subject);

    public bool IsEnabled => PublicKey is not null && PrivateKey is not null && Subject is not null;

    public IReadOnlyList<string> MissingRequirements()
    {
        var missing = new List<string>();
        if (PublicKey is null) missing.Add("WebPush:Vapid:PublicKey");
        if (PrivateKey is null) missing.Add("WebPush:Vapid:PrivateKey");
        if (Subject is null) missing.Add("WebPush:Vapid:Subject");
        return missing;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
