using System.Globalization;

namespace RentalPlatform.Infrastructure.Services;

// Bound from "Email". Every value is a plain string on purpose (M-016): compose maps unset
// variables to "", and a non-nullable bool/int binder throws on "" - which at startup is a crashloop
// and an outage. All parsing is tolerant and lives in the properties below.
public sealed class EmailOptions
{
    public const string SectionName = "Email";

    public const string DefaultFrom = "DoRent <no-reply@dorent.am>";
    public const int DefaultSendBudgetLimit = 100;
    public static readonly TimeSpan DefaultSendBudgetWindow = TimeSpan.FromHours(24);

    public string? Provider { get; init; }
    public string? From { get; init; }
    public ResendOptions Resend { get; init; } = new();
    public SendBudgetOptions SendBudget { get; init; } = new();

    public bool IsResend => string.Equals(Provider?.Trim(), "Resend", StringComparison.OrdinalIgnoreCase);

    public string EffectiveFrom => string.IsNullOrWhiteSpace(From) ? DefaultFrom : From.Trim();

    public bool HasApiKey => !string.IsNullOrWhiteSpace(Resend.ApiKey);

    public int EffectiveSendBudgetLimit =>
        int.TryParse(SendBudget.Limit?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var limit) && limit > 0
            ? limit
            : DefaultSendBudgetLimit;

    public TimeSpan EffectiveSendBudgetWindow =>
        double.TryParse(SendBudget.WindowHours?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var hours)
        && hours > 0 && hours < 24 * 365
            ? TimeSpan.FromHours(hours)
            : DefaultSendBudgetWindow;
}

public sealed class ResendOptions
{
    public string? ApiKey { get; init; }
}

// Sized to the provider quota (Resend free tier is 100 emails/day). Counts emails SENT, never requests.
public sealed class SendBudgetOptions
{
    public string? Limit { get; init; }
    public string? WindowHours { get; init; }
}

// Bound from "App".
public sealed class AppOptions
{
    public const string SectionName = "App";

    public string? PublicBaseUrl { get; init; }
}
