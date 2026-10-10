using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Infrastructure.Services;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Infrastructure;

// The outside-world adapters of ADR-028/029. M-013: a component every other test fakes must be
// tested for real at least once - so the REAL ResendEmailSender runs here against a stubbed
// HttpMessageHandler, and the REAL Apple token validation runs against a self-signed JWKS.
public sealed class ResendEmailSenderTests
{
    private const string ApiKey = "re_test_SECRET_key_123";
    private const string LinkToken = "LINK_TOKEN_abcdefghijklmnopqrstuvwxyz0123456789";

    private static readonly EmailMessage Message = new(
        "to@test.local",
        "Subject",
        $"<a href=\"https://dorent.am/auth/verify-email#token={LinkToken}\">x</a>",
        $"https://dorent.am/auth/verify-email#token={LinkToken}");

    private static EmailOptions Options(string? apiKey = ApiKey, string? from = null) =>
        new() { Provider = "Resend", From = from, Resend = new ResendOptions { ApiKey = apiKey } };

    private static ResendEmailSender Sender(
        StubHttpMessageHandler handler, ListLogger<ResendEmailSender> logger, EmailOptions? options = null, TimeSpan? timeout = null) =>
        new(new HttpClient(handler), Microsoft.Extensions.Options.Options.Create(options ?? Options()), logger, timeout);

    private static StubHttpMessageHandler Responding(HttpStatusCode status, string body = "{}") =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) }));

    private static void AssertNothingSecretLogged(ListLogger<ResendEmailSender> logger)
    {
        var text = logger.AllText();
        Assert.DoesNotContain(LinkToken, text, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiKey, text, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Posts_The_Message_To_The_Resend_Endpoint_With_A_Bearer_Key_And_No_Idempotency_Key()
    {
        var handler = Responding(HttpStatusCode.OK);
        var logger = new ListLogger<ResendEmailSender>();

        await Sender(handler, logger).SendAsync(Message);

        var (request, body) = Assert.Single(handler.Calls);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.resend.com/emails", request.RequestUri!.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal(ApiKey, request.Headers.Authorization.Parameter);
        Assert.False(request.Headers.Contains("Idempotency-Key"));

        using var json = JsonDocument.Parse(body);
        Assert.Equal(EmailOptions.DefaultFrom, json.RootElement.GetProperty("from").GetString());
        Assert.Equal("to@test.local", json.RootElement.GetProperty("to")[0].GetString());
        Assert.Equal("Subject", json.RootElement.GetProperty("subject").GetString());
        Assert.Equal(Message.HtmlBody, json.RootElement.GetProperty("html").GetString());
        Assert.Equal(Message.TextBody, json.RootElement.GetProperty("text").GetString());

        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Information && entry.Message.Contains("200"));
        AssertNothingSecretLogged(logger);
    }

    [Fact]
    public async Task Uses_The_Configured_From_Address()
    {
        var handler = Responding(HttpStatusCode.OK);

        await Sender(handler, new ListLogger<ResendEmailSender>(), Options(from: "DoRent <hello@dorent.am>")).SendAsync(Message);

        using var json = JsonDocument.Parse(handler.Calls.Single().Body);
        Assert.Equal("DoRent <hello@dorent.am>", json.RootElement.GetProperty("from").GetString());
    }

    [Theory]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task A_Failure_Status_Is_Logged_At_Error_Without_Throwing_And_Without_Retrying(HttpStatusCode status)
    {
        // The body is echoed back by some providers; it must never reach the log.
        var handler = Responding(status, body: $"{{\"message\":\"rejected {LinkToken} {ApiKey}\"}}");
        var logger = new ListLogger<ResendEmailSender>();

        await Sender(handler, logger).SendAsync(Message);

        Assert.Single(handler.Calls); // no retry
        var error = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Error);
        Assert.Contains(((int)status).ToString(), error.Message);
        AssertNothingSecretLogged(logger);
    }

    [Fact]
    public async Task A_Network_Failure_Is_Swallowed_And_Logged_Without_The_Secrets()
    {
        var handler = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("connection refused"));
        var logger = new ListLogger<ResendEmailSender>();

        await Sender(handler, logger).SendAsync(Message);

        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Error);
        AssertNothingSecretLogged(logger);
    }

    [Fact]
    public async Task A_Hung_Provider_Is_Cut_Off_By_The_Senders_Own_Timeout()
    {
        var handler = new StubHttpMessageHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var logger = new ListLogger<ResendEmailSender>();

        var started = DateTime.UtcNow;
        await Sender(handler, logger, timeout: TimeSpan.FromMilliseconds(200)).SendAsync(Message);

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Error && entry.Message.Contains("timed out"));
        AssertNothingSecretLogged(logger);
    }

    [Fact]
    public async Task The_Request_Being_Aborted_Does_Not_Cancel_The_Send()
    {
        // The user row is committed before the send; a client that hung up must not cost them the email.
        var handler = Responding(HttpStatusCode.OK);
        using var aborted = new CancellationTokenSource();
        aborted.Cancel();

        await Sender(handler, new ListLogger<ResendEmailSender>()).SendAsync(Message, aborted.Token);

        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task Without_An_Api_Key_Nothing_Is_Sent()
    {
        var handler = Responding(HttpStatusCode.OK);
        var logger = new ListLogger<ResendEmailSender>();

        await Sender(handler, logger, Options(apiKey: "  ")).SendAsync(Message);

        Assert.Empty(handler.Calls);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Error);
    }
}

public sealed class EmailSettingsAndBudgetTests
{
    private static EmailVerificationSettings Settings(
        string environment, string? provider, string? apiKey, string? baseUrl) =>
        new(
            Microsoft.Extensions.Options.Options.Create(new EmailOptions { Provider = provider, Resend = new ResendOptions { ApiKey = apiKey } }),
            Microsoft.Extensions.Options.Options.Create(new AppOptions { PublicBaseUrl = baseUrl }),
            new FakeHostEnvironment(environment));

    [Theory]
    [InlineData("Resend", "re_key", "https://dorent.am", true)]
    [InlineData("resend", "re_key", "https://dorent.am/", true)]
    [InlineData("Log", "re_key", "https://dorent.am", false)]
    [InlineData(null, "re_key", "https://dorent.am", false)]
    [InlineData("", "re_key", "https://dorent.am", false)]
    [InlineData("Resend", "", "https://dorent.am", false)]
    [InlineData("Resend", null, "https://dorent.am", false)]
    [InlineData("Resend", "re_key", "http://dorent.am", false)]
    [InlineData("Resend", "re_key", "", false)]
    [InlineData("Resend", "re_key", null, false)]
    [InlineData("Resend", "re_key", "dorent.am", false)]
    [InlineData("Resend", "re_key", "garbage ::", false)]
    public void Production_Gate_Needs_Resend_A_Key_And_An_Https_Base_Url(
        string? provider, string? apiKey, string? baseUrl, bool operational)
    {
        Assert.Equal(operational, Settings("Production", provider, apiKey, baseUrl).IsOperational);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Staging")]
    [InlineData("IntegrationTest")]
    public void Outside_Production_The_Gate_Is_Always_Open(string environment)
    {
        Assert.True(Settings(environment, null, null, null).IsOperational);
    }

    [Fact]
    public void The_Link_Comes_From_Config_Puts_The_Token_In_The_Fragment_And_Handles_A_Trailing_Slash()
    {
        var link = Settings("Production", "Resend", "k", "https://dorent.am/").BuildVerificationLink("TOK");

        Assert.Equal("https://dorent.am/auth/verify-email#token=TOK", link);
    }

    [Fact]
    public void Without_A_Base_Url_Outside_Production_The_Link_Falls_Back_To_The_Dev_Origin()
    {
        var link = Settings("Development", null, null, null).BuildVerificationLink("TOK");

        Assert.Equal("http://localhost:4200/auth/verify-email#token=TOK", link);
    }

    [Fact]
    public async Task Startup_Check_Logs_Critical_Once_In_A_Misconfigured_Production_And_Never_Throws()
    {
        var logger = new ListLogger<EmailConfigurationStartupCheck>();
        var settings = Settings("Production", "Log", "", "http://insecure");
        var check = new EmailConfigurationStartupCheck(settings, new FakeHostEnvironment("Production"), logger);

        await check.StartAsync(CancellationToken.None);

        var critical = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Critical);
        Assert.Contains("Provider", critical.Message);
        Assert.Contains("ApiKey", critical.Message);
        Assert.Contains("PublicBaseUrl", critical.Message);
    }

    [Fact]
    public async Task Startup_Check_Is_Silent_When_Production_Is_Configured_Or_When_Not_Production()
    {
        var good = new ListLogger<EmailConfigurationStartupCheck>();
        await new EmailConfigurationStartupCheck(
            Settings("Production", "Resend", "k", "https://dorent.am"), new FakeHostEnvironment("Production"), good)
            .StartAsync(CancellationToken.None);

        var dev = new ListLogger<EmailConfigurationStartupCheck>();
        await new EmailConfigurationStartupCheck(
            Settings("Development", null, null, null), new FakeHostEnvironment("Development"), dev)
            .StartAsync(CancellationToken.None);

        Assert.Empty(good.Entries);
        Assert.Empty(dev.Entries);
    }

    // ---- Options parsing (M-016: never throw on an empty or garbage value) ----

    [Theory]
    [InlineData(null, 100)]
    [InlineData("", 100)]
    [InlineData("   ", 100)]
    [InlineData("abc", 100)]
    [InlineData("-5", 100)]
    [InlineData("0", 100)]
    [InlineData("1.5", 100)]
    [InlineData("250", 250)]
    public void Send_Budget_Limit_Parses_Tolerantly(string? raw, int expected)
    {
        var options = new EmailOptions { SendBudget = new SendBudgetOptions { Limit = raw } };

        Assert.Equal(expected, options.EffectiveSendBudgetLimit);
    }

    [Theory]
    [InlineData(null, 24)]
    [InlineData("", 24)]
    [InlineData("nope", 24)]
    [InlineData("0", 24)]
    [InlineData("-1", 24)]
    [InlineData("1", 1)]
    [InlineData("0.5", 0.5)]
    public void Send_Budget_Window_Parses_Tolerantly(string? raw, double expectedHours)
    {
        var options = new EmailOptions { SendBudget = new SendBudgetOptions { WindowHours = raw } };

        Assert.Equal(TimeSpan.FromHours(expectedHours), options.EffectiveSendBudgetWindow);
    }

    [Fact]
    public void Options_Bind_From_Configuration_With_Empty_Values_Without_Throwing()
    {
        // What compose does to an unset variable: the key is present and EMPTY.
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Email:Provider"] = "",
                ["Email:From"] = "",
                ["Email:Resend:ApiKey"] = "",
                ["Email:SendBudget:Limit"] = "",
                ["Email:SendBudget:WindowHours"] = "",
                ["App:PublicBaseUrl"] = ""
            })
            .Build();

        var email = configuration.GetSection(EmailOptions.SectionName).Get<EmailOptions>()!;
        var app = configuration.GetSection(AppOptions.SectionName).Get<AppOptions>()!;

        Assert.False(email.IsResend);
        Assert.Equal(EmailOptions.DefaultFrom, email.EffectiveFrom);
        Assert.Equal(100, email.EffectiveSendBudgetLimit);
        Assert.Equal(TimeSpan.FromHours(24), email.EffectiveSendBudgetWindow);
        Assert.Equal(string.Empty, app.PublicBaseUrl);
    }

    // ---- Composition: the transport is chosen by Email:Provider ----

    private static IEmailSender ResolveSender(string? provider)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = "Server=.;Database=NeverConnected;",
                ["Jwt:Issuer"] = "i", ["Jwt:Audience"] = "a", ["Jwt:SecretKey"] = new string('k', 40),
                ["FileStorage:ListingsImagesPath"] = "uploads/listings",
                ["Email:Provider"] = provider,
                ["Email:Resend:ApiKey"] = "re_key"
            })
            .Build();

        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new FakeHostEnvironment("Production"));
        RentalPlatform.Infrastructure.DependencyInjection.ServiceCollectionExtensions.AddInfrastructure(services, configuration);

        var provider_ = services.BuildServiceProvider(new Microsoft.Extensions.DependencyInjection.ServiceProviderOptions { ValidateScopes = true });
        using var scope = provider_.CreateScope();
        return scope.ServiceProvider.GetRequiredService<IEmailSender>();
    }

    [Theory]
    [InlineData("Resend", typeof(ResendEmailSender))]
    [InlineData("resend", typeof(ResendEmailSender))]
    [InlineData("Log", typeof(LoggingEmailSender))]
    [InlineData("", typeof(LoggingEmailSender))]
    [InlineData(null, typeof(LoggingEmailSender))]
    [InlineData("Mailgun", typeof(LoggingEmailSender))]
    public void The_Provider_Setting_Selects_The_Transport_And_Defaults_To_Logging(string? provider, Type expected)
    {
        Assert.IsType(expected, ResolveSender(provider));
    }

    // ---- Global send budget ----

    private static EmailSendBudget Budget(int limit, double hours, TimeProvider clock, ListLogger<EmailSendBudget> logger) =>
        new(
            Microsoft.Extensions.Options.Options.Create(new EmailOptions
            {
                SendBudget = new SendBudgetOptions { Limit = limit.ToString(), WindowHours = hours.ToString(System.Globalization.CultureInfo.InvariantCulture) }
            }),
            clock,
            logger);

    [Fact]
    public void Budget_Allows_Up_To_The_Limit_Then_Refuses_With_A_Critical_Log()
    {
        var logger = new ListLogger<EmailSendBudget>();
        var budget = Budget(3, 24, new FakeTimeProvider(), logger);

        Assert.True(budget.TryAcquire());
        Assert.True(budget.TryAcquire());
        Assert.True(budget.TryAcquire());
        Assert.Empty(logger.Entries);

        Assert.False(budget.TryAcquire());
        Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Critical);
    }

    [Fact]
    public void Budget_Starts_A_New_Window_After_The_Old_One_Ends()
    {
        var clock = new FakeTimeProvider();
        var budget = Budget(1, 24, clock, new ListLogger<EmailSendBudget>());

        Assert.True(budget.TryAcquire());
        Assert.False(budget.TryAcquire());

        clock.Advance(TimeSpan.FromHours(24) - TimeSpan.FromSeconds(1));
        Assert.False(budget.TryAcquire());

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(budget.TryAcquire());
    }

    [Fact]
    public async Task Budget_Is_Exact_Under_Concurrency()
    {
        var budget = Budget(25, 24, new FakeTimeProvider(), new ListLogger<EmailSendBudget>());

        var results = await Task.WhenAll(Enumerable.Range(0, 200).Select(_ => Task.Run(budget.TryAcquire)));

        Assert.Equal(25, results.Count(granted => granted));
    }

    [Fact]
    public void Cap_Monitor_Writes_A_Warning_With_The_User_Id_And_Nothing_Else()
    {
        var logger = new ListLogger<EmailVerificationMonitor>();
        var userId = Guid.NewGuid();

        new EmailVerificationMonitor(logger).PerRecipientCapReached(userId);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal($"Email verification per-recipient cap reached for user {userId}.", entry.Message);
    }

    // ---- Logging transport ----

    [Fact]
    public async Task Logging_Sender_Prints_The_Link_Outside_Production()
    {
        var logger = new ListLogger<LoggingEmailSender>();

        await new LoggingEmailSender(new FakeHostEnvironment("Development"), logger)
            .SendAsync(new EmailMessage("a@test.local", "Subj", "<p/>", "https://x/auth/verify-email#token=DEVTOKEN"));

        Assert.Contains("DEVTOKEN", logger.AllText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Logging_Sender_In_Production_Logs_Only_That_A_Send_Was_Suppressed()
    {
        var logger = new ListLogger<LoggingEmailSender>();

        await new LoggingEmailSender(new FakeHostEnvironment("Production"), logger)
            .SendAsync(new EmailMessage("a@test.local", "Subj", "<p/>", "https://x/auth/verify-email#token=PRODTOKEN"));

        var text = logger.AllText();
        Assert.Contains("suppressed", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PRODTOKEN", text, StringComparison.Ordinal);
        Assert.DoesNotContain("a@test.local", text, StringComparison.Ordinal);
    }

    // ---- Content ----

    [Theory]
    [InlineData("en", "Confirm your email on DoRent")]
    [InlineData("EN", "Confirm your email on DoRent")]
    [InlineData(null, "Confirm your email on DoRent")]
    [InlineData("", "Confirm your email on DoRent")]
    [InlineData("fr", "Confirm your email on DoRent")]
    [InlineData("ru", "Подтвердите email в DoRent")]
    [InlineData("ru-RU", "Подтвердите email в DoRent")]
    [InlineData("hy", "Հաստատեք Ձեր էլ. փոստը DoRent-ում")]
    [InlineData("hy_AM", "Հաստատեք Ձեր էլ. փոստը DoRent-ում")]
    public void Verification_Email_Is_Localised_With_An_English_Fallback(string? language, string subject)
    {
        var message = VerificationEmailTemplate.Render("a@test.local", language, "https://dorent.am/auth/verify-email#token=T");

        Assert.Equal(subject, message.Subject);
        Assert.Contains("https://dorent.am/auth/verify-email#token=T", message.TextBody, StringComparison.Ordinal);
        Assert.Contains("https://dorent.am/auth/verify-email#token=T", message.HtmlBody, StringComparison.Ordinal);
        Assert.Equal("a@test.local", message.To);
    }

    [Fact]
    public async Task EmailService_Hands_The_Rendered_Message_To_The_Transport_And_Never_Throws()
    {
        var sender = new CaptureEmailSender();
        var service = new EmailService(sender, new ListLogger<EmailService>());

        await service.SendEmailVerificationAsync("a@test.local", "ru", "https://x/auth/verify-email#token=T");
        Assert.Single(sender.Messages);

        var failing = new EmailService(new ThrowingSender(), new ListLogger<EmailService>());
        await failing.SendEmailVerificationAsync("a@test.local", "en", "https://x/auth/verify-email#token=T"); // must not throw
    }

    private sealed class ThrowingSender : IEmailSender
    {
        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("transport exploded");
    }
}

// Apple's email_verified rule (ADR-028 section 10), through the REAL validator: a genuine RS256 token
// verified against a JWKS the test serves from a stubbed handler.
public sealed class AppleIdentityTokenValidationTests
{
    private const string Issuer = "https://appleid.apple.com";
    private const string Audience = "test.client.id";

    private static readonly RSA SigningKey = RSA.Create(2048);

    private static string Token(Action<List<Claim>> claims)
    {
        var list = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, "apple-user-1"),
            new(JwtRegisteredClaimNames.Email, "someone@privaterelay.appleid.com")
        };
        claims(list);

        var credentials = new SigningCredentials(new RsaSecurityKey(SigningKey) { KeyId = "k1" }, SecurityAlgorithms.RsaSha256);
        var token = new JwtSecurityToken(Issuer, Audience, list, DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(30), credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static ExternalIdentityTokenValidator Validator()
    {
        var parameters = SigningKey.ExportParameters(false);
        var jwks = JsonSerializer.Serialize(new
        {
            keys = new[]
            {
                new
                {
                    kty = "RSA", kid = "k1", use = "sig", alg = "RS256",
                    n = Base64UrlEncoder.Encode(parameters.Modulus),
                    e = Base64UrlEncoder.Encode(parameters.Exponent)
                }
            }
        });
        var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(jwks) }));

        return new ExternalIdentityTokenValidator(
            Microsoft.Extensions.Options.Options.Create(new ExternalAuthOptions
            {
                Apple = new AppleExternalAuthOptions { Issuer = Issuer, JwksUrl = "https://appleid.apple.com/auth/keys", ValidAudiences = new[] { Audience } }
            }),
            new StubHttpClientFactory(handler),
            TimeProvider.System);
    }

    private static async Task<string?> EmailFor(Action<List<Claim>> claims)
    {
        var result = await Validator().ValidateAsync("apple", Token(claims));
        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal("apple-user-1", result.Value!.ProviderUserId);
        return result.Value.Email;
    }

    [Fact]
    public async Task Email_Is_Kept_When_email_verified_Is_A_JSON_Bool_True() =>
        Assert.Equal("someone@privaterelay.appleid.com",
            await EmailFor(claims => claims.Add(new Claim("email_verified", "true", ClaimValueTypes.Boolean))));

    [Fact]
    public async Task Email_Is_Kept_When_email_verified_Is_The_String_true() =>
        Assert.Equal("someone@privaterelay.appleid.com",
            await EmailFor(claims => claims.Add(new Claim("email_verified", "true"))));

    [Fact]
    public async Task Email_Is_Dropped_When_email_verified_Is_Missing() =>
        Assert.Null(await EmailFor(_ => { }));

    [Theory]
    [InlineData("false")]
    [InlineData("0")]
    [InlineData("yes")]
    [InlineData("")]
    public async Task Email_Is_Dropped_When_email_verified_Is_False_Or_Garbage(string value) =>
        Assert.Null(await EmailFor(claims => claims.Add(new Claim("email_verified", value))));

    [Fact]
    public async Task Email_Is_Dropped_When_email_verified_Is_A_JSON_Bool_False() =>
        Assert.Null(await EmailFor(claims => claims.Add(new Claim("email_verified", "false", ClaimValueTypes.Boolean))));
}
