using Google.Apis.Auth;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RentalPlatform.Application.DTOs;
using RentalPlatform.Infrastructure.Services;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Auth;

// ADR-030 section 2: the nonce store and the order of checks in the Google validator.
public sealed class ExternalAuthNonceStoreTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    private static ExternalAuthNonceStore Store(int capacity = ExternalAuthNonceStore.DefaultCapacity) =>
        new(NullLogger<ExternalAuthNonceStore>.Instance, capacity);

    [Fact]
    public void Issue_Returns_A_Base64Url_Nonce_Valid_For_Five_Minutes()
    {
        var store = Store();

        var issued = store.TryIssue(T0)!;

        Assert.Equal(T0.AddMinutes(5), issued.ExpiresAt);
        Assert.Equal(43, issued.Nonce.Length); // 32 bytes, base64url, no padding
        Assert.Matches("^[A-Za-z0-9_-]+$", issued.Nonce);
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void Two_Issued_Nonces_Differ()
    {
        var store = Store();
        Assert.NotEqual(store.TryIssue(T0)!.Nonce, store.TryIssue(T0)!.Nonce);
    }

    [Fact]
    public void An_Issued_Nonce_Is_Consumed_Once()
    {
        var store = Store();
        var nonce = store.TryIssue(T0)!.Nonce;

        Assert.True(store.TryConsume(nonce, T0.AddSeconds(1)));
        Assert.False(store.TryConsume(nonce, T0.AddSeconds(2)));
        Assert.Equal(0, store.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("never-issued")]
    public void Missing_Or_Unknown_Nonce_Is_Refused(string? nonce)
    {
        var store = Store();
        store.TryIssue(T0);

        Assert.False(store.TryConsume(nonce, T0));
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void A_Nonce_Longer_Than_64_Characters_Is_Refused_Without_Touching_The_Store()
    {
        var store = Store();
        var nonce = store.TryIssue(T0)!.Nonce;

        Assert.False(store.TryConsume(new string('a', 65), T0));
        Assert.Equal(1, store.Count);
        Assert.True(store.TryConsume(nonce, T0));
    }

    [Fact]
    public void An_Expired_Nonce_Is_Refused_And_Gone()
    {
        var store = Store();
        var issued = store.TryIssue(T0)!;

        Assert.False(store.TryConsume(issued.Nonce, issued.ExpiresAt)); // expiry instant is already too late
        Assert.Equal(0, store.Count);
        Assert.False(store.TryConsume(issued.Nonce, T0)); // consumed by the failed attempt
    }

    [Fact]
    public void Just_Before_Expiry_The_Nonce_Still_Works()
    {
        var store = Store();
        var issued = store.TryIssue(T0)!;

        Assert.True(store.TryConsume(issued.Nonce, issued.ExpiresAt.AddTicks(-1)));
    }

    [Fact]
    public async Task One_Hundred_Parallel_Consumes_Of_One_Nonce_Succeed_Exactly_Once()
    {
        var store = Store();
        var nonce = store.TryIssue(T0)!.Nonce;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var racers = Enumerable.Range(0, 100).Select(_ => Task.Run(async () =>
        {
            await gate.Task;
            return store.TryConsume(nonce, T0.AddSeconds(1));
        })).ToArray();
        await Task.Delay(50);
        gate.SetResult();

        Assert.Equal(1, (await Task.WhenAll(racers)).Count(won => won));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void When_Full_Issue_Refuses_And_Never_Evicts_A_Live_Nonce()
    {
        var store = Store(capacity: 3);
        var live = Enumerable.Range(0, 3).Select(_ => store.TryIssue(T0)!.Nonce).ToArray();

        Assert.Null(store.TryIssue(T0));
        Assert.Null(store.TryIssue(T0));
        Assert.Equal(3, store.Count);

        // Every existing nonce is still consumable, and consuming one frees exactly one slot.
        Assert.All(live, nonce => Assert.True(store.TryConsume(nonce, T0.AddSeconds(1))));
        Assert.Equal(0, store.Count);
        Assert.NotNull(store.TryIssue(T0));
    }

    [Fact]
    public async Task Concurrent_Issue_Never_Exceeds_Capacity()
    {
        var store = Store(capacity: 50);

        var issued = await Task.WhenAll(Enumerable.Range(0, 400).Select(_ => Task.Run(() => store.TryIssue(T0))));

        Assert.Equal(50, issued.Count(item => item is not null));
        Assert.Equal(50, store.Count);
    }

    [Fact]
    public void Sweep_Removes_Only_Expired_Entries_And_Keeps_The_Counter_Consistent()
    {
        var store = Store();
        var old = store.TryIssue(T0)!;
        var fresh = store.TryIssue(T0.AddMinutes(4))!;

        var removed = store.SweepExpired(T0.AddMinutes(6)); // old expired at +5, fresh at +9

        Assert.Equal(1, removed);
        Assert.Equal(1, store.Count);
        Assert.False(store.TryConsume(old.Nonce, T0.AddMinutes(6)));
        Assert.True(store.TryConsume(fresh.Nonce, T0.AddMinutes(6)));
        Assert.Equal(0, store.Count);
        Assert.Equal(0, store.SweepExpired(T0.AddHours(1)));
    }

    [Fact]
    public void The_Raw_Nonce_Is_Not_Used_As_The_Dictionary_Key()
    {
        // Behavioural proxy: a store holds only hashes, so presenting the SHA-256 hex of an issued
        // nonce must not consume it.
        var store = Store();
        var nonce = store.TryIssue(T0)!.Nonce;
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(nonce)));

        Assert.False(store.TryConsume(hash, T0));
        Assert.True(store.TryConsume(nonce, T0));
    }
}

public sealed class GoogleTokenValidatorTests
{
    private const string Audience = "123456789-test.apps.googleusercontent.com";
    private static readonly DateTimeOffset T0 = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    private sealed class Rig
    {
        public Rig(string[]? audiences = null)
        {
            Clock = new FakeTimeProvider(T0);
            Store = new ExternalAuthNonceStore(NullLogger<ExternalAuthNonceStore>.Instance);
            Verifier = new StubGoogleIdTokenVerifier();
            Validator = new ExternalIdentityTokenValidator(
                Options.Create(new ExternalAuthOptions
                {
                    Google = new GoogleExternalAuthOptions { ValidAudiences = audiences ?? new[] { Audience } }
                }),
                new StubHttpClientFactory(new StubHttpMessageHandler((_, _) => throw new InvalidOperationException("no http"))),
                Clock,
                Verifier,
                Store);
        }

        public FakeTimeProvider Clock { get; }
        public ExternalAuthNonceStore Store { get; }
        public StubGoogleIdTokenVerifier Verifier { get; }
        public ExternalIdentityTokenValidator Validator { get; }

        public string IssueNonce() => Store.TryIssue(Clock.GetUtcNow())!.Nonce;

        public string Sign(string token, string? nonce, Action<GoogleJsonWebSignature.Payload>? tweak = null)
        {
            var payload = new GoogleJsonWebSignature.Payload
            {
                Subject = "google-sub-1",
                Email = "someone@gmail.com",
                EmailVerified = true,
                GivenName = "Gina",
                FamilyName = "Google",
                Name = "Gina Google",
                Picture = "https://example.org/p.png",
                Nonce = nonce
            };
            tweak?.Invoke(payload);
            Verifier.Tokens[token] = payload;
            return token;
        }
    }

    [Fact]
    public async Task An_Invalid_Token_Is_Refused_And_The_Nonce_Store_Is_Untouched()
    {
        var rig = new Rig();
        var nonce = rig.IssueNonce();

        var result = await rig.Validator.ValidateAsync("google", "garbage");

        Assert.False(result.IsSuccess);
        Assert.Equal("auth.external_invalid_token", result.Error!.Code);
        Assert.Equal(1, rig.Store.Count);
        Assert.True(rig.Store.TryConsume(nonce, T0)); // still there
    }

    [Fact]
    public async Task A_Valid_Token_With_An_Unknown_Nonce_Is_Invalid()
    {
        var rig = new Rig();
        var token = rig.Sign("t1", "never-issued");

        var result = await rig.Validator.ValidateAsync("google", token);

        Assert.Equal("auth.external_invalid_token", result.Error!.Code);
    }

    [Fact]
    public async Task A_Valid_Token_Without_A_Nonce_Is_Invalid()
    {
        var rig = new Rig();
        var token = rig.Sign("t1", null);

        var result = await rig.Validator.ValidateAsync("google", token);

        Assert.Equal("auth.external_invalid_token", result.Error!.Code);
    }

    [Fact]
    public async Task A_Valid_Token_With_An_Issued_Nonce_Succeeds_Once_And_The_Replay_Is_Invalid()
    {
        var rig = new Rig();
        var token = rig.Sign("t1", rig.IssueNonce());

        var first = await rig.Validator.ValidateAsync("google", token);
        var replay = await rig.Validator.ValidateAsync("google", token);

        Assert.True(first.IsSuccess);
        Assert.Equal("google-sub-1", first.Value!.ProviderUserId);
        Assert.Equal("someone@gmail.com", first.Value.Email);
        Assert.Equal("auth.external_invalid_token", replay.Error!.Code);
        Assert.Equal(0, rig.Store.Count);
    }

    [Fact]
    public async Task An_Expired_Nonce_Makes_The_Token_Invalid()
    {
        var rig = new Rig();
        var token = rig.Sign("t1", rig.IssueNonce());
        rig.Clock.Advance(TimeSpan.FromMinutes(5));

        var result = await rig.Validator.ValidateAsync("google", token);

        Assert.Equal("auth.external_invalid_token", result.Error!.Code);
    }

    [Fact]
    public async Task Two_Parallel_Sign_Ins_With_One_Token_Yield_Exactly_One_Success()
    {
        var rig = new Rig();
        var token = rig.Sign("t1", rig.IssueNonce());

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => rig.Validator.ValidateAsync("google", token)));

        Assert.Equal(1, results.Count(result => result.IsSuccess));
    }

    [Fact]
    public async Task The_Nonce_Stays_Consumed_Even_When_A_Later_Step_Refuses_The_User()
    {
        // ADR-030 section 2: the validator succeeded, so the nonce is spent regardless of what the
        // resolution step later answers (403/409).
        var rig = new Rig();
        var nonce = rig.IssueNonce();
        var token = rig.Sign("t1", nonce);

        Assert.True((await rig.Validator.ValidateAsync("google", token)).IsSuccess);

        Assert.False(rig.Store.TryConsume(nonce, T0));
    }

    [Fact]
    public async Task An_Unverified_Email_Is_Not_Returned()
    {
        var rig = new Rig();
        var token = rig.Sign("t1", rig.IssueNonce(), payload => payload.EmailVerified = false);

        var result = await rig.Validator.ValidateAsync("google", token);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value!.Email);
    }

    [Fact]
    public async Task Names_And_The_Hosted_Domain_Are_Passed_Through_Untouched()
    {
        var rig = new Rig();
        var token = rig.Sign("t1", rig.IssueNonce(), payload =>
        {
            payload.GivenName = null;
            payload.FamilyName = null;
            payload.Name = "Full Name";
            payload.HostedDomain = "example.org";
        });

        var info = (await rig.Validator.ValidateAsync("google", token)).Value!;

        Assert.Null(info.FirstName);
        Assert.Null(info.LastName);
        Assert.Equal("Full Name", info.FullName);
        Assert.Equal("example.org", info.HostedDomain);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("set-google-client-id-in-environment")]
    [InlineData("your-google-client-id.apps.googleusercontent.com")]
    [InlineData("SET-something|  |Your-other")]
    public async Task Missing_Or_Placeholder_Audiences_Mean_Not_Configured_503_Before_Anything_Else(string audiences)
    {
        var rig = new Rig(audiences.Split('|'));
        var token = rig.Sign("t1", rig.IssueNonce());

        var result = await rig.Validator.ValidateAsync("google", token);

        Assert.Equal("auth.external_provider_unavailable", result.Error!.Code);
        Assert.Equal(0, rig.Verifier.Calls);
        Assert.Equal(1, rig.Store.Count);
    }

    [Fact]
    public async Task Only_The_Real_Audiences_Reach_The_Verifier()
    {
        var rig = new Rig(new[] { " set-me ", Audience, "" });
        var token = rig.Sign("t1", rig.IssueNonce());

        await rig.Validator.ValidateAsync("google", token);

        Assert.Equal(new[] { Audience }, rig.Verifier.LastAudiences);
    }

    [Fact]
    public async Task An_Unsupported_Provider_Message_Does_Not_Echo_The_Input()
    {
        var rig = new Rig();

        var result = await rig.Validator.ValidateAsync("<script>alert(1)</script>", "x");

        Assert.Equal("auth.external_provider_unsupported", result.Error!.Code);
        Assert.DoesNotContain("script", result.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExternalAuthOptions_Reports_Configuration_State()
    {
        Assert.False(new GoogleExternalAuthOptions().IsConfigured);
        Assert.False(new GoogleExternalAuthOptions { ValidAudiences = new[] { "set-google-client-id-in-environment" } }.IsConfigured);
        Assert.True(new GoogleExternalAuthOptions { ValidAudiences = new[] { Audience } }.IsConfigured);
    }
}

public sealed class GoogleConfigurationStartupAndLoggingTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    private static Task RunCheckAsync(string environment, string[] audiences, ListLogger<ExternalAuthConfigurationStartupCheck> logger) =>
        new ExternalAuthConfigurationStartupCheck(
            Options.Create(new ExternalAuthOptions { Google = new GoogleExternalAuthOptions { ValidAudiences = audiences } }),
            new FakeHostEnvironment(environment),
            logger).StartAsync(CancellationToken.None);

    [Theory]
    [InlineData("")]
    [InlineData("set-google-client-id-in-environment")]
    public async Task Startup_Check_Logs_Critical_Once_In_Production_Without_A_Real_Client_Id_And_Never_Throws(string audience)
    {
        var logger = new ListLogger<ExternalAuthConfigurationStartupCheck>();

        await RunCheckAsync("Production", new[] { audience }, logger);

        var critical = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Critical);
        Assert.Contains("ValidAudiences", critical.Message);
    }

    [Fact]
    public async Task Startup_Check_Is_Silent_When_Configured_Or_Outside_Production()
    {
        var configured = new ListLogger<ExternalAuthConfigurationStartupCheck>();
        await RunCheckAsync("Production", new[] { "123-real.apps.googleusercontent.com" }, configured);
        var development = new ListLogger<ExternalAuthConfigurationStartupCheck>();
        await RunCheckAsync("Development", Array.Empty<string>(), development);

        Assert.Empty(configured.Entries);
        Assert.Empty(development.Entries);
    }

    [Fact]
    public void A_Full_Store_Logs_Critical_At_Most_Once_A_Minute_And_Never_Logs_A_Nonce()
    {
        var logger = new ListLogger<ExternalAuthNonceStore>();
        var store = new ExternalAuthNonceStore(logger, capacity: 1);
        var live = store.TryIssue(T0)!;

        for (var i = 0; i < 5; i++)
        {
            Assert.Null(store.TryIssue(T0.AddSeconds(i)));
        }

        Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Critical);
        Assert.Null(store.TryIssue(T0.AddSeconds(30)));
        Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Critical);
        Assert.Null(store.TryIssue(T0.AddSeconds(61)));
        Assert.Equal(2, logger.Entries.Count(entry => entry.Level == LogLevel.Critical));
        Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains(live.Nonce, StringComparison.Ordinal));
    }
}
