using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using RentalPlatform.Application.Abstractions;

namespace RentalPlatform.Infrastructure.Services;

// ADR-028 section 9 (the production email gate) and section 6 (the link). Never throws: a bad value
// closes the gate, it does not stop the process (M-016).
public sealed class EmailVerificationSettings : IEmailVerificationSettings
{
    // Only used outside Production, where the gate is open and the logging sender prints the link.
    private const string DevelopmentFallbackBaseUrl = "http://localhost:4200";

    private readonly EmailOptions _email;
    private readonly AppOptions _app;
    private readonly IHostEnvironment _environment;

    public EmailVerificationSettings(
        IOptions<EmailOptions> email,
        IOptions<AppOptions> app,
        IHostEnvironment environment)
    {
        _email = email.Value;
        _app = app.Value;
        _environment = environment;
    }

    public bool IsOperational => !_environment.IsProduction() || MissingRequirements().Count == 0;

    // What a Production deployment is missing, by name. Never contains a value (the key is a secret).
    public IReadOnlyList<string> MissingRequirements()
    {
        var missing = new List<string>();
        if (!_email.IsResend)
        {
            missing.Add("Email:Provider is not 'Resend'");
        }

        if (!_email.HasApiKey)
        {
            missing.Add("Email:Resend:ApiKey is empty");
        }

        if (!IsHttpsBaseUrl(_app.PublicBaseUrl))
        {
            missing.Add("App:PublicBaseUrl is not an https URL");
        }

        return missing;
    }

    public string BuildVerificationLink(string token)
    {
        var baseUrl = string.IsNullOrWhiteSpace(_app.PublicBaseUrl)
            ? DevelopmentFallbackBaseUrl
            : _app.PublicBaseUrl.Trim();

        // The token sits in the fragment so it never reaches access logs or Referer (ADR-028 section 6).
        return $"{baseUrl.TrimEnd('/')}/auth/verify-email#token={token}";
    }

    private static bool IsHttpsBaseUrl(string? value) =>
        Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
}
