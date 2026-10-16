using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using RentalPlatform.Application.DTOs;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Infrastructure.Services;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Auth;

// ADR-035 at the service level: the REAL AuthService, PasswordResetService, stores and email
// templates over SQLite, with only the transport, the clock, the production gate and the send budget
// as doubles (AuthHarness). The races that need a real SQL Server are in PasswordResetConcurrencyTests.
public sealed class PasswordResetFlowTests
{
    private const string Email = "forgetful@test.local";
    private const string OldPassword = "OldPassword123";
    private const string NewPassword = "BrandNewPassword9";
    private static readonly Guid UserId = new("a5000000-0000-0000-0000-000000000001");

    private static User Verified(bool blocked = false, bool confirmed = true, string? hash = null, string? language = "en") =>
        TestData.User(UserId, Email, isBlocked: blocked, passwordHash: hash ?? $"hashed:{OldPassword}",
            isEmailConfirmed: confirmed, preferredLanguage: language);

    private static ForgotPasswordRequest Forgot(string? email = Email, string? language = null) =>
        new() { Email = email, PreferredLanguage = language };

    private static UserToken ResetToken(string raw, DateTime createdAt, TokenPurpose purpose = TokenPurpose.PasswordReset,
        DateTime? consumedAt = null, TimeSpan? lifetime = null) => new()
    {
        Id = Guid.NewGuid(),
        UserId = UserId,
        Purpose = purpose,
        TokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(raw)),
        CreatedAt = createdAt,
        ExpiresAt = createdAt + (lifetime ?? TimeSpan.FromMinutes(30)),
        ConsumedAt = consumedAt
    };

    private static async Task<List<UserToken>> TokensAsync(SqliteTestDatabase db, TokenPurpose purpose = TokenPurpose.PasswordReset)
    {
        await using var context = db.CreateContext();
        return await context.UserTokens.AsNoTracking().Where(token => token.Purpose == purpose).OrderBy(token => token.CreatedAt).ToListAsync();
    }

    private static async Task<User> UserAsync(SqliteTestDatabase db)
    {
        await using var context = db.CreateContext();
        return await context.Users.AsNoTracking().SingleAsync(user => user.Id == UserId);
    }

    private static ResetPasswordRequest Reset(string token, string password = NewPassword) =>
        new() { Token = token, NewPassword = password };

    // Requests a link as the real flow does and reads the raw token back from the captured email.
    private static async Task<string> IssueAsync(AuthHarness h)
    {
        var result = await h.Auth.ForgotPasswordAsync(Forgot());
        Assert.True(result.IsSuccess);
        return h.Sender.LastTokenFor(Email)!;
    }

    // ---- forgot-password ---------------------------------------------------------------------------

    [Fact]
    public async Task Forgot_For_A_Verified_Account_Stores_One_Hashed_Token_And_Sends_One_Email()
    {
        using var db = new SqliteTestDatabase();
        await db.SeedAsync(Verified());
        await using var context = db.CreateContext();
        var h = new AuthHarness(context);

        var result = await h.Auth.ForgotPasswordAsync(Forgot(" Forgetful@Test.Local "));

        Assert.True(result.IsSuccess);
        var token = Assert.Single(await TokensAsync(db));
        Assert.Equal(TokenPurpose.PasswordReset, token.Purpose);
        Assert.Null(token.ConsumedAt);
        Assert.Equal(h.Clock.GetUtcNow().UtcDateTime.AddMinutes(30), token.ExpiresAt);

        var message = Assert.Single(h.Sender.Messages);
        Assert.Equal(Email, message.To);
        var raw = CaptureEmailSender.ExtractToken(message.TextBody)!;
        Assert.Equal(43, raw.Length); // 32 bytes, base64url, no padding
        Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes(raw)), token.TokenHash);
        // The link is a FRAGMENT on the reset page, from configuration (ADR-035 section 1).
        Assert.Contains("https://test.local/auth/reset-password#token=", message.TextBody, StringComparison.Ordinal);
        Assert.Contains("https://test.local/auth/reset-password#token=", message.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("?token=", message.TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Forgot_Email_Carries_No_User_Controlled_Data_And_No_Sign_Out_Claim()
    {
        using var db = new SqliteTestDatabase();
        var user = Verified();
        user.FirstName = "Mallory<script>";
        user.LastName = "Phisher&Co";
        await db.SeedAsync(user);
        await using var context = db.CreateContext();
        var h = new AuthHarness(context);

        await h.Auth.ForgotPasswordAsync(Forgot());

        var message = Assert.Single(h.Sender.Messages);
        foreach (var body in new[] { message.HtmlBody, message.TextBody, message.Subject })
        {
            Assert.DoesNotContain("Mallory", body, StringComparison.Ordinal);
            Assert.DoesNotContain("Phisher", body, StringComparison.Ordinal);
            Assert.DoesNotContain(Email, body, StringComparison.Ordinal);
            Assert.DoesNotContain("devices", body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("blocked")]
    [InlineData("pending")]
    [InlineData("malformed")]
    [InlineData("empty")]
    [InlineData("null")]
    public async Task Forgot_Is_A_Silent_Success_That_Sends_And_Stores_Nothing(string scenario)
    {
        using var db = new SqliteTestDatabase();
        await db.SeedAsync(scenario switch
        {
            "blocked" => Verified(blocked: true),
            "pending" => Verified(confirmed: false),
            _ => Verified()
        });
        await using var context = db.CreateContext();
        var h = new AuthHarness(context);

        var email = scenario switch
        {
            "unknown" => "nobody@test.local",
            "malformed" => "not-an-email<>",
            "empty" => "   ",
            "null" => null,
            _ => Email
        };

        var result = await h.Auth.ForgotPasswordAsync(Forgot(email));

        Assert.True(result.IsSuccess);
        Assert.Empty(h.Sender.Messages);
        Assert.Empty(await TokensAsync(db));
    }

    [Fact]
    public async Task Forgot_Inside_The_Cooldown_Is_Silent_And_After_It_Rotates_The_Token()
    {
        using var db = new SqliteTestDatabase();
        await db.SeedAsync(Verified());
        await using var context = db.CreateContext();
        var h = new AuthHarness(context);

        var first = await IssueAsync(h);
        h.Clock.Advance(TimeSpan.FromSeconds(59));
        await h.Auth.ForgotPasswordAsync(Forgot());
        Assert.Equal(1, h.Sender.CountFor(Email));
        Assert.Single(await TokensAsync(db));

        h.Clock.Advance(TimeSpan.FromSeconds(2)); // 61 s since the first
        await h.Auth.ForgotPasswordAsync(Forgot());
        var second = h.Sender.LastTokenFor(Email)!;

        Assert.Equal(2, h.Sender.CountFor(Email));
        Assert.NotEqual(first, second);
        var tokens = await TokensAsync(db);
        Assert.Equal(2, tokens.Count);
        Assert.NotNull(tokens[0].ConsumedAt); // revoked, never deleted
        Assert.Null(tokens[1].ConsumedAt);

        // The previous link is dead, the new one works.
        var dead = await h.Auth.CheckResetTokenAsync(new ResetPasswordCheckRequest { Token = first });
        Assert.Equal("auth.reset_token_invalid", dead.Error!.Code);
        Assert.True((await h.Auth.CheckResetTokenAsync(new ResetPasswordCheckRequest { Token = second })).IsSuccess);
    }

    [Fact]
    public async Task Forgot_Stops_Sending_After_Five_Reset_Emails_In_24_Hours()
    {
        using var db = new SqliteTestDatabase();
        await db.SeedAsync(Verified());
        await using var context = db.CreateContext();
        var h = new AuthHarness(context);

        for (var i = 0; i < 5; i++)
        {
            await h.Auth.ForgotPasswordAsync(Forgot());
            h.Clock.Advance(TimeSpan.FromSeconds(61));
        }

        Assert.Equal(5, h.Sender.CountFor(Email));

        var sixth = await h.Auth.ForgotPasswordAsync(Forgot());

        Assert.True(sixth.IsSuccess);
        Assert.Equal(5, h.Sender.CountFor(Email));

        // The window rolls: the oldest token leaves the last 24 hours.
        h.Clock.Advance(TimeSpan.FromHours(24));
        await h.Auth.ForgotPasswordAsync(Forgot());
        Assert.Equal(6, h.Sender.CountFor(Email));
    }

    [Fact]
    public async Task The_Reset_Cap_Is_Independent_Of_The_Verification_Cap()
    {
        using var db = new SqliteTestDatabase();
        await db.SeedAsync(Verified());
        await using var context = db.CreateContext();
        var h = new AuthHarness(context);
        var now = h.Clock.GetUtcNow().UtcDateTime;

        // Verification squatting: five consumed verification tokens inside the window.
        await using (var seed = db.CreateContext())
        {
            for (var i = 0; i < 5; i++)
            {
                seed.UserTokens.Add(ResetToken($"v{i}", now.AddMinutes(-10 - i), TokenPurpose.EmailVerification, consumedAt: now.AddMinutes(-9)));
            }

            await seed.SaveChangesAsync();
        }

        await h.Auth.ForgotPasswordAsync(Forgot());
        Assert.Equal(1, h.Sender.CountFor(Email));

        // Counted per purpose: the reset consumed one reset slot and none of the verification ones.
        Assert.Equal(1, await h.Store.CountTokensCreatedSinceAsync(UserId, TokenPurpose.PasswordReset, now.AddHours(-24)));
        Assert.Equal(5, await h.Store.CountTokensCreatedSinceAsync(UserId, TokenPurpose.EmailVerification, now.AddHours(-24)));
    }

    [Fact]
    public async Task Forgot_With_An_Exhausted_Global_Budget_Answers_503_For_Existing_And_Unknown_Emails_Alike()
    {
        using var db = new SqliteTestDatabase();
        await db.SeedAsync(Verified());
        await using var context = db.CreateContext();
        var h = new AuthHarness(context);
        var liveToken = await IssueAsync(h);
        h.Clock.Advance(TimeSpan.FromMinutes(5)); // past the cooldown: without the gate a new token would be issued
        h.Sender.Clear();
        h.Budget.Remaining = 0;

        var existing = await h.Auth.ForgotPasswordAsync(Forgot());
        var unknown = await h.Auth.ForgotPasswordAsync(Forgot("nobody@test.local"));
        var empty = await h.Auth.ForgotPasswordAsync(Forgot(""));

        foreach (var result in new[] { existing, unknown, empty })
        {
            Assert.False(result.IsSuccess);
            Assert.Equal("auth.password_reset_unavailable", result.Error!.Code);
        }

        Assert.Empty(h.Sender.Messages);

        // No token issued and the previous live token NOT revoked.
        var tokens = await TokensAsync(db);
        var only = Assert.Single(tokens);
        Assert.Null(only.ConsumedAt);
        Assert.True((await h.Auth.CheckResetTokenAsync(new ResetPasswordCheckRequest { Token = liveToken })).IsSuccess);
    }

    [Fact]
    public async Task A_Refused_Issue_Never_Consumes_Send_Budget()
    {
        using var db = new SqliteTestDatabase();
        await db.SeedAsync(Verified());
        await using var context = db.CreateContext();
        var h = new AuthHarness(context);
        h.Budget.Remaining = 10;

        await h.Auth.ForgotPasswordAsync(Forgot());
        Assert.Equal(1, h.Budget.Acquired);

        // Cooldown refusal (service pre-check) and a store-level refusal (cap) both leave the budget alone.
        h.Clock.Advance(TimeSpan.FromSeconds(10));
        await h.Auth.ForgotPasswordAsync(Forgot());
        Assert.Equal(1, h.Budget.Acquired);

        var now = h.Clock.GetUtcNow().UtcDateTime;
        var limits = new RentalPlatform.Application.Abstractions.TokenLimits(now.AddSeconds(-60), now.AddHours(-24), 1);
        var reserved = false;
        var outcome = await h.PasswordResetStore.TryIssueAsync(
            ResetToken("x", now), now, limits, () => reserved = true);
        Assert.Equal(RentalPlatform.Application.Abstractions.IssueResetTokenOutcome.Refused, outcome);
        Assert.False(reserved);
        Assert.Equal(1, h.Budget.Acquired);
    }

    [Fact]
    public async Task A_Budget_That_Runs_Out_After_The_Check_Rolls_The_Issue_Back_And_Answers_503()
    {
        using var db = new SqliteTestDatabase();
        await db.SeedAsync(Verified());
        await using var context = db.CreateContext();
        var h = new AuthHarness(context);
        var previous = await IssueAsync(h);
        h.Clock.Advance(TimeSpan.FromMinutes(5));
        h.Sender.Clear();
        h.Budget.DenyAcquire = true; // HasCapacity still true, the acquire fails: the race

        var result = await h.Auth.ForgotPasswordAsync(Forgot());

        Assert.False(result.IsSuccess);
        Assert.Equal("auth.password_reset_unavailable", result.Error!.Code);
        Assert.Empty(h.Sender.Messages);

        // Consistent: still exactly the previous, live token; its link still works.
        var only = Assert.Single(await TokensAsync(db));
        Assert.Null(only.ConsumedAt);
        Assert.True((await h.Auth.CheckResetTokenAsync(new ResetPasswordCheckRequest { Token = previous })).IsSuccess);
    }

    [Fact]
    public async Task Verification_Register_And_Resend_Are_Unchanged_When_The_Budget_Is_Exhausted()
    {
        using var db = new SqliteTestDatabase();
        await using var context = db.CreateContext();
        var h = new AuthHarness(context);
        h.Budget.Remaining = 0;

        var register = await h.Auth.RegisterAsync(AuthHarness.Register("late@test.local"));

        Assert.True(register.IsSuccess); // ADR-028 section 8: still a 201, just no email
        Assert.Empty(h.Sender.Messages);
    }

    [Fact]
    public async Task A_Reset_Cap_Hit_Logs_A_Warning_With_The_User_Id_And_Never_The_Address()
    {
        using var db = new SqliteTestDatabase();
        await db.SeedAsync(Verified());
        await using var context = db.CreateContext();
        var h = new AuthHarness(context);
        var logger = new CollectingLogger<RentalPlatform.Infrastructure.Services.EmailVerificationMonitor>();
        var service = new RentalPlatform.Application.Services.PasswordResetService(
            h.Store, h.PasswordResetStore, h.PasswordHasher,
            new RentalPlatform.Infrastructure.Services.EmailService(h.Sender,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<RentalPlatform.Infrastructure.Services.EmailService>.Instance),
            h.Settings, h.Budget, new RentalPlatform.Infrastructure.Services.EmailVerificationMonitor(logger), h.Clock);

        for (var i = 0; i < 5; i++)
        {
            await service.ForgotAsync(Email, null);
            h.Clock.Advance(TimeSpan.FromSeconds(61));
        }

        Assert.Empty(logger.Entries);
        var sixth = await service.ForgotAsync(Email, null);

        Assert.True(sixth.IsSuccess); // still 202
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Warning, entry.Level);
        Assert.Contains(UserId.ToString(), entry.Text);
        Assert.DoesNotContain(Email, entry.Text, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class CollectingLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        public List<(Microsoft.Extensions.Logging.LogLevel Level, string Text)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    [Fact]
    public async Task Forgot_Draws_On_The_Same_Send_Budget_As_Verification_Email()
    {
        using var db = new SqliteTestDatabase();
        await db.SeedAsync(Verified());
        await using var context = db.CreateContext();
        var h = new AuthHarness(context);
        h.Budget.Remaining = 1;

        await h.Auth.ForgotPasswordAsync(Forgot());
        Assert.Equal(1, h.Budget.Acquired);
        Assert.Equal(0, h.Budget.Remaining);

        // The one shared budget is now empty, so a registration cannot send either.
        await h.Auth.RegisterAsync(AuthHarness.Register("late@test.local"));
        Assert.Single(h.Sender.Messages);
    }

    [Fact]
    public async Task Forgot_Is_Unavailable_While_The_Production_Gate_Is_Closed_And_Creates_Nothing()
    {
        using var db = new SqliteTestDatabase();
        await db.SeedAsync(Verified());
        await using var context = db.CreateContext();
        var h = new AuthHarness(context);
        h.Settings.IsOperational = false;

        var result = await h.Auth.ForgotPasswordAsync(Forgot());

        Assert.False(result.IsSuccess);
        Assert.Equal("auth.password_reset_unavailable", result.Error!.Code);
        Assert.Empty(await TokensAsync(db));
        Assert.Empty(h.Sender.Messages);
    }

    [Fact]
    public async Task Forgot_Works_For_An_External_Only_Account()
    {
        using var db = new SqliteTestDatabase();
        await db.SeedAsync(Verified(hash: string.Empty));
        await using var context = db.CreateContext();
        var h = new AuthHarness(context);

        await h.Auth.ForgotPasswordAsync(Forgot());

        Assert.Equal(1, h.Sender.CountFor(Email));
    }

    [Theory]
    [InlineData("hy", "ru", "DoRent-ի")]   // request wins over the stored language
    [InlineData(null, "ru", "Сброс")]        // then the stored language
    [InlineData(null, null, "Reset")]        // then English
    [InlineData("fr", "hy", "DoRent-ի")]   // an unsupported request language falls to the stored one
    [InlineData("ru-RU", "en", "Сброс")]     // region suffixes are accepted
    public async Task Forgot_Picks_The_Email_Language_Request_Then_Stored_Then_English(
        string? requested, string? stored, string expectedSubjectStart)
    {
        using var db = new SqliteTestDatabase();
        await db.SeedAsync(Verified(language: stored));
        await using var context = db.CreateContext();
        var h = new AuthHarness(context);

        await h.Auth.ForgotPasswordAsync(Forgot(language: requested));

        var message = Assert.Single(h.Sender.Messages);
        Assert.StartsWith(expectedSubjectStart, message.Subject, StringComparison.Ordinal);
    }

    // ---- check -------------------------------------------------------------------------------------

    [Fact]
    public async Task Check_Is_Read_Only_The_Link_Still_Resets_Afterwards()
    {
        using var db = new SqliteTestDatabase();
        await db.SeedAsync(Verified());
        await using var context = db.CreateContext();
        var h = new AuthHarness(context);
        var raw = await IssueAsync(h);

        for (var i = 0; i < 3; i++)
        {
            Assert.True((await h.Auth.CheckResetTokenAsync(new ResetPasswordCheckRequest { Token = raw })).IsSuccess);
        }

        Assert.Null(Assert.Single(await TokensAsync(db)).ConsumedAt);
        var reset = await h.Auth.ResetPasswordAsync(Reset(raw));
        Assert.True(reset.IsSuccess, reset.Error?.Code);
    }

    [Fact]
    public async Task Check_Reports_Expired_And_Invalid_With_Distinct_Codes()
    {
        using var db = new SqliteTestDatabase();
        await db.SeedAsync(Verified());
        await using var context = db.CreateContext();
        var h = new AuthHarness(context);
        var raw = await IssueAsync(h);

        var unknown = await h.Auth.CheckResetTokenAsync(new ResetPasswordCheckRequest { Token = "does-not-exist" });
        Assert.Equal("auth.reset_token_invalid", unknown.Error!.Code);

        h.Clock.Advance(TimeSpan.FromMinutes(31));
        var expired = await h.Auth.CheckResetTokenAsync(new ResetPasswordCheckRequest { Token = raw });
        Assert.Equal("auth.reset_token_expired", expired.Error!.Code);
    }

    // ---- reset-password ----------------------------------------------------------------------------

    [Fact]
    public async Task Reset_Changes_The_Password_Consumes_The_Token_And_Signs_The_User_In()
    {
        using var db = new SqliteTestDatabase();
        await db.SeedAsync(Verified());
        await using var context = db.CreateContext();
        var h = new AuthHarness(context);
        var raw = await IssueAsync(h);

        var result = await h.Auth.ResetPasswordAsync(Reset(raw));

        Assert.True(result.IsSuccess, result.Error?.Code);
        Assert.Equal($"fake-token:{UserId}", result.Value!.AccessToken);
        Assert.Equal(Email, result.Value.User.Email);

        Assert.Equal($"hashed:{NewPassword}", (await UserAsync(db)).PasswordHash);
        Assert.NotNull(Assert.Single(await TokensAsync(db)).ConsumedAt);

        var login = await h.Auth.LoginAsync(new LoginRequest { Email = Email, Password = NewPassword });
        Assert.True(login.IsSuccess);
        var oldLogin = await h.Auth.LoginAsync(new LoginRequest { Email = Email, Password = OldPassword });
        Assert.False(oldLogin.IsSuccess);
    }

    [Fact]
    public async Task Reset_Is_Single_Use()
    {
        using var db = new SqliteTestDatabase();
        await db.SeedAsync(Verified());
        await using var context = db.CreateContext();
        var h = new AuthHarness(context);
        var raw = await IssueAsync(h);
        Assert.True((await h.Auth.ResetPasswordAsync(Reset(raw))).IsSuccess);

        var again = await h.Auth.ResetPasswordAsync(Reset(raw, "YetAnotherPassword1"));

        Assert.Equal("auth.reset_token_invalid", again.Error!.Code);
        Assert.Equal($"hashed:{NewPassword}", (await UserAsync(db)).PasswordHash);
    }

    [Fact]
    public async Task Reset_With_An_Expired_Token_Is_Expired_And_Changes_Nothing()
    {
        using var db = new SqliteTestDatabase();
        await db.SeedAsync(Verified());
        await using var context = db.CreateContext();
        var h = new AuthHarness(context);
        var raw = await IssueAsync(h);
        h.Clock.Advance(TimeSpan.FromMinutes(30)); // ExpiresAt <= now

        var result = await h.Auth.ResetPasswordAsync(Reset(raw));

        Assert.Equal("auth.reset_token_expired", result.Error!.Code);
        Assert.Equal($"hashed:{OldPassword}", (await UserAsync(db)).PasswordHash);
    }

    [Fact]
    public async Task Reset_With_An_Unknown_Or_Wrong_Purpose_Token_Is_Invalid()
    {
        using var db = new SqliteTestDatabase();
        var now = new FakeTimeProvider().GetUtcNow().UtcDateTime;
        await db.SeedAsync(Verified(), ResetToken("verification-link", now, TokenPurpose.EmailVerification, lifetime: TimeSpan.FromHours(24)));
        await using var context = db.CreateContext();
        var h = new AuthHarness(context);

        var unknown = await h.Auth.ResetPasswordAsync(Reset("nope"));
        // A verification link must never work as a reset link: lookups match Purpose too.
        var wrongPurpose = await h.Auth.ResetPasswordAsync(Reset("verification-link"));

        Assert.Equal("auth.reset_token_invalid", unknown.Error!.Code);
        Assert.Equal("auth.reset_token_invalid", wrongPurpose.Error!.Code);
        Assert.Equal($"hashed:{OldPassword}", (await UserAsync(db)).PasswordHash);
    }

    [Fact]
    public async Task Reset_With_A_Revoked_Token_Is_Invalid()
    {
        using var db = new SqliteTestDatabase();
        await db.SeedAsync(Verified());
        await using var context = db.CreateContext();
        var h = new AuthHarness(context);
        var first = await IssueAsync(h);
        h.Clock.Advance(TimeSpan.FromSeconds(61));
        await h.Auth.ForgotPasswordAsync(Forgot()); // revokes `first`

        var result = await h.Auth.ResetPasswordAsync(Reset(first));

        Assert.Equal("auth.reset_token_invalid", result.Error!.Code);
    }

    [Fact]
    public async Task Reset_Is_Refused_For_A_User_Blocked_After_The_Link_Was_Sent()
    {
        using var db = new SqliteTestDatabase();
        await db.SeedAsync(Verified());
        await using var context = db.CreateContext();
        var h = new AuthHarness(context);
        var raw = await IssueAsync(h);
        await using (var admin = db.CreateContext())
        {
            await admin.Users.Where(user => user.Id == UserId).ExecuteUpdateAsync(set => set.SetProperty(user => user.IsBlocked, true));
        }

        var check = await h.Auth.CheckResetTokenAsync(new ResetPasswordCheckRequest { Token = raw });
        var result = await h.Auth.ResetPasswordAsync(Reset(raw));

        Assert.Equal("auth.reset_token_invalid", check.Error!.Code);
        Assert.Equal("auth.reset_token_invalid", result.Error!.Code);
        Assert.Equal($"hashed:{OldPassword}", (await UserAsync(db)).PasswordHash);
        Assert.Null(Assert.Single(await TokensAsync(db)).ConsumedAt);
    }

    [Fact]
    public async Task Reset_Is_Refused_For_A_Pending_Account_And_Never_Verifies_It()
    {
        using var db = new SqliteTestDatabase();
        var now = new FakeTimeProvider().GetUtcNow().UtcDateTime;
        await db.SeedAsync(Verified(confirmed: false), ResetToken("pending-link", now));
        await using var context = db.CreateContext();
        var h = new AuthHarness(context);

        var result = await h.Auth.ResetPasswordAsync(Reset("pending-link"));

        Assert.Equal("auth.reset_token_invalid", result.Error!.Code);
        var user = await UserAsync(db);
        Assert.False(user.IsEmailConfirmed);
        Assert.Equal($"hashed:{OldPassword}", user.PasswordHash);
    }

    [Fact]
    public async Task The_Store_Commit_Itself_Refuses_A_Blocked_User_And_Rolls_Back_Whole()
    {
        // The predicates are the last line of defence when the account changes between the service's
        // read and the commit: prove them at the store, bypassing the service pre-checks.
        using var db = new SqliteTestDatabase();
        var now = new FakeTimeProvider().GetUtcNow().UtcDateTime;
        var token = ResetToken("raw", now);
        await db.SeedAsync(Verified(blocked: true), token);
        await using var context = db.CreateContext();
        var store = new RentalPlatform.Infrastructure.Persistence.PasswordResetStore(context);

        var committed = await store.TryCommitResetAsync(token.Id, UserId, "hashed:x", now);

        Assert.False(committed);
        Assert.Equal($"hashed:{OldPassword}", (await UserAsync(db)).PasswordHash);
        Assert.Null(Assert.Single(await TokensAsync(db)).ConsumedAt);
    }

    [Fact]
    public async Task The_Store_Commit_Rolls_The_Password_Back_When_The_Token_Is_Already_Consumed()
    {
        using var db = new SqliteTestDatabase();
        var now = new FakeTimeProvider().GetUtcNow().UtcDateTime;
        var token = ResetToken("raw", now.AddMinutes(-5), consumedAt: now.AddMinutes(-1));
        await db.SeedAsync(Verified(), token);
        await using var context = db.CreateContext();
        var store = new RentalPlatform.Infrastructure.Persistence.PasswordResetStore(context);

        var committed = await store.TryCommitResetAsync(token.Id, UserId, "hashed:x", now);

        Assert.False(committed);
        Assert.Equal($"hashed:{OldPassword}", (await UserAsync(db)).PasswordHash); // the UPDATE on Users was rolled back
    }

    [Fact]
    public async Task Reset_Rejects_A_Password_Longer_Than_72_Bytes_Counted_In_Bytes_Not_Characters()
    {
        using var db = new SqliteTestDatabase();
        await db.SeedAsync(Verified());
        await using var context = db.CreateContext();
        var h = new AuthHarness(context);
        var raw = await IssueAsync(h);

        var armenian72 = new string('ա', 36);   // 36 chars = 72 bytes: allowed
        var armenian74 = new string('ա', 37);   // 37 chars = 74 bytes: too long, though well under 72 characters
        Assert.Equal(74, Encoding.UTF8.GetByteCount(armenian74));

        var tooLong = await h.Auth.ResetPasswordAsync(Reset(raw, armenian74));
        Assert.Equal("auth.password_too_long", tooLong.Error!.Code);
        Assert.Null(Assert.Single(await TokensAsync(db)).ConsumedAt); // the link survives a rejected password

        var ok = await h.Auth.ResetPasswordAsync(Reset(raw, armenian72));
        Assert.True(ok.IsSuccess, ok.Error?.Code);
    }

    [Fact]
    public async Task An_External_Only_User_Can_Set_A_First_Password_And_Log_In_With_It_Using_Real_Bcrypt()
    {
        using var db = new SqliteTestDatabase();
        await db.SeedAsync(Verified(hash: string.Empty));
        await using var context = db.CreateContext();
        var h = new AuthHarness(context, passwordHasher: new BcryptPasswordHasher());

        // Before: an empty hash cannot log in (and must not throw, M-013).
        var before = await h.Auth.LoginAsync(new LoginRequest { Email = Email, Password = NewPassword });
        Assert.False(before.IsSuccess);

        var raw = await IssueAsync(h);
        var reset = await h.Auth.ResetPasswordAsync(Reset(raw));
        Assert.True(reset.IsSuccess, reset.Error?.Code);

        var stored = (await UserAsync(db)).PasswordHash;
        Assert.StartsWith("$2", stored, StringComparison.Ordinal);
        var login = await h.Auth.LoginAsync(new LoginRequest { Email = Email, Password = NewPassword });
        Assert.True(login.IsSuccess);
    }

    [Fact]
    public async Task Check_And_Reset_Share_What_Live_Means_A_Used_Link_Fails_Both()
    {
        using var db = new SqliteTestDatabase();
        await db.SeedAsync(Verified());
        await using var context = db.CreateContext();
        var h = new AuthHarness(context);
        var raw = await IssueAsync(h);
        await h.Auth.ResetPasswordAsync(Reset(raw));

        var check = await h.Auth.CheckResetTokenAsync(new ResetPasswordCheckRequest { Token = raw });

        Assert.Equal("auth.reset_token_invalid", check.Error!.Code);
    }
}
