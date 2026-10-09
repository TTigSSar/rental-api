using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.DTOs;
using RentalPlatform.Application.Services;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Infrastructure.Persistence;
using RentalPlatform.Infrastructure.Services;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Auth;

// ADR-028 end to end at the service level: the REAL AuthService, EmailVerificationService, stores and
// email templates over SQLite, with only the transport, the clock, the production gate and the send
// budget as doubles (AuthHarness). SQLite is enough here because every statement is portable; the
// behaviours that need real concurrent writers or SQL Server error numbers are in
// EmailVerificationConcurrencyTests.
public sealed class EmailVerificationFlowTests
{
    private const string Email = "pending.user@test.local";
    private const string Password = "Sufficient1Password";

    private static readonly Guid PendingId = new("e1000000-0000-0000-0000-000000000001");

    private static User Pending(string email = Email, string password = Password, bool blocked = false) =>
        TestData.User(PendingId, email, isBlocked: blocked, passwordHash: $"hashed:{password}", isEmailConfirmed: false);

    private static async Task<List<UserToken>> TokensAsync(SqliteTestDatabase db, Guid userId)
    {
        await using var context = db.CreateContext();
        return await context.UserTokens.AsNoTracking().Where(token => token.UserId == userId).OrderBy(token => token.CreatedAt).ToListAsync();
    }

    private static async Task<User> UserAsync(SqliteTestDatabase db, string email)
    {
        await using var context = db.CreateContext();
        return await context.Users.AsNoTracking().SingleAsync(user => user.Email == email);
    }

    private static TokenLimits Limits(DateTime now) => new(now.AddSeconds(-60), now.AddHours(-24), 5);

    private static UserToken ActiveTokenFor(Guid userId, string raw, DateTime createdAt) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        Purpose = TokenPurpose.EmailVerification,
        TokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(raw)),
        CreatedAt = createdAt,
        ExpiresAt = createdAt.AddHours(24)
    };

    // ---- Register: a brand-new email ----------------------------------------------------------------

    [Fact]
    public async Task Register_Creates_A_Pending_User_One_Token_And_Sends_One_Email()
    {
        using var db = new SqliteTestDatabase();
        await using var context = db.CreateContext();
        var h = new AuthHarness(context);

        var result = await h.Auth.RegisterAsync(AuthHarness.Register(" New.User@Test.Local "));

        Assert.True(result.IsSuccess);
        Assert.Equal("new.user@test.local", result.Value!.Email);
        Assert.True(result.Value.VerificationRequired);

        var user = await UserAsync(db, "new.user@test.local");
        Assert.False(user.IsEmailConfirmed);
        Assert.Null(user.EmailConfirmedAt);

        var token = Assert.Single(await TokensAsync(db, user.Id));
        Assert.Equal(TokenPurpose.EmailVerification, token.Purpose);
        Assert.Null(token.ConsumedAt);
        Assert.Equal(h.Clock.GetUtcNow().UtcDateTime.AddHours(24), token.ExpiresAt);
        Assert.Equal(32, token.TokenHash.Length);

        var message = Assert.Single(h.Sender.Messages);
        Assert.Equal("new.user@test.local", message.To);
    }

    [Fact]
    public async Task Register_Stores_Only_The_Sha256_Of_The_Emailed_Token()
    {
        using var db = new SqliteTestDatabase();
        await using var context = db.CreateContext();
        var h = new AuthHarness(context);

        await h.Auth.RegisterAsync(AuthHarness.Register());

        var raw = h.Sender.LastTokenFor("new.user@test.local");
        Assert.NotNull(raw);
        Assert.Equal(43, raw!.Length); // 32 bytes, base64url, no padding
        var stored = Assert.Single(await db.CreateContext().UserTokens.AsNoTracking().ToListAsync());
        Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes(raw)), stored.TokenHash);
        Assert.DoesNotContain(raw, Convert.ToBase64String(stored.TokenHash), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Register_Email_Carries_The_Link_And_No_User_Controlled_Data()
    {
        using var db = new SqliteTestDatabase();
        await using var context = db.CreateContext();
        var h = new AuthHarness(context);

        await h.Auth.RegisterAsync(AuthHarness.Register(firstName: "Mallory<script>", lastName: "Phisher&Co"));

        var message = Assert.Single(h.Sender.Messages);
        foreach (var body in new[] { message.HtmlBody, message.TextBody, message.Subject })
        {
            Assert.DoesNotContain("Mallory", body, StringComparison.Ordinal);
            Assert.DoesNotContain("Phisher", body, StringComparison.Ordinal);
        }

        Assert.Contains("https://test.local/auth/verify-email#token=", message.TextBody, StringComparison.Ordinal);
        Assert.Contains("https://test.local/auth/verify-email#token=", message.HtmlBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Register_Applies_The_Home_Point_After_The_User_Exists()
    {
        using var db = new SqliteTestDatabase();
        await using var context = db.CreateContext();
        var h = new AuthHarness(context);

        var (latitude, longitude) = TestData.KentronPoint;
        var result = await h.Auth.RegisterAsync(AuthHarness.Register(latitude: latitude, longitude: longitude));

        Assert.True(result.IsSuccess);
        var user = await UserAsync(db, "new.user@test.local");
        Assert.Equal(latitude, user.HomeLatitude);
        Assert.NotNull(user.HomePublicLatitude);
        Assert.NotNull(user.HomeDistrictId);
        Assert.Single(h.Sender.Messages);
    }

    [Fact]
    public async Task Register_Uses_The_Users_Language_For_The_Email_With_English_Fallback()
    {
        using var db = new SqliteTestDatabase();
        await using var context = db.CreateContext();
        var h = new AuthHarness(context);

        await h.Auth.RegisterAsync(AuthHarness.Register("ru.user@test.local", language: "ru"));
        await h.Auth.RegisterAsync(AuthHarness.Register("xx.user@test.local", language: "xx"));

        Assert.StartsWith("Подтвердите", h.Sender.Messages[0].Subject, StringComparison.Ordinal);
        Assert.StartsWith("Confirm", h.Sender.Messages[1].Subject, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Register_Gate_Closed_Returns_Unavailable_And_Creates_Nothing()
    {
        using var db = new SqliteTestDatabase();
        await using var context = db.CreateContext();
        var h = new AuthHarness(context);
        h.Settings.IsOperational = false;

        var result = await h.Auth.RegisterAsync(AuthHarness.Register());

        Assert.False(result.IsSuccess);
        Assert.Equal("auth.registration_unavailable", result.Error!.Code);
        Assert.Empty(await db.CreateContext().Users.ToListAsync());
        Assert.Empty(await db.CreateContext().UserTokens.ToListAsync());
        Assert.Empty(h.Sender.Messages);
    }

    [Fact]
    public async Task Register_With_Exhausted_Send_Budget_Still_Succeeds_But_Sends_Nothing()
    {
        using var db = new SqliteTestDatabase();
        await using var context = db.CreateContext();
        var h = new AuthHarness(context);
        h.Budget.Remaining = 0;

        var result = await h.Auth.RegisterAsync(AuthHarness.Register());

        // Never a 429 for somebody else's traffic: the account and its token exist, resend recovers.
        Assert.True(result.IsSuccess);
        Assert.Single(await db.CreateContext().UserTokens.ToListAsync());
        Assert.Empty(h.Sender.Messages);
    }

    [Fact]
    public async Task Register_Duplicate_Insert_Is_A_Conflict_Not_An_Exception()
    {
        using var db = new SqliteTestDatabase();
        await db.SeedAsync(TestData.User(Guid.NewGuid(), "taken@test.local"));
        await using var context = db.CreateContext();
        var store = new EmailVerificationStore(context);

        // The state the service saw can be stale; the unique index is what decides.
        var added = await store.TryAddUserAsync(
            TestData.User(Guid.NewGuid(), "taken@test.local", isEmailConfirmed: false), token: null);

        Assert.False(added);
        Assert.Empty(context.ChangeTracker.Entries()); // detached again: the context is reusable
        Assert.Equal(1, await context.Users.CountAsync());
    }

    // ---- Register: replacing a pending registration (D2) ---------------------------------------------

    [Fact]
    public async Task Reregister_After_Cooldown_Replaces_Password_Profile_And_Home_Point_And_Revokes_Old_Token()
    {
        using var db = new SqliteTestDatabase();
        await using var context = db.CreateContext();
        var h = new AuthHarness(context);

        var (latitude, longitude) = TestData.KentronPoint;
        await h.Auth.RegisterAsync(AuthHarness.Register(
            password: "AttackerPassword1", firstName: "Squatter", phone: "+374 99 000111", latitude: latitude, longitude: longitude));
        var oldToken = h.Sender.LastTokenFor("new.user@test.local")!;
        var original = await UserAsync(db, "new.user@test.local");
        Assert.NotNull(original.HomeLatitude);

        h.Clock.Advance(TimeSpan.FromSeconds(61));
        var result = await h.Auth.RegisterAsync(AuthHarness.Register(
            password: "OwnerPassword22", firstName: "Owner", phone: "+374 99 222333"));

        Assert.True(result.IsSuccess);
        var replaced = await UserAsync(db, "new.user@test.local");
        Assert.Equal(original.Id, replaced.Id);
        Assert.Equal("hashed:OwnerPassword22", replaced.PasswordHash);
        Assert.Equal("Owner", replaced.FirstName);
        Assert.Equal("+374 99 222333", replaced.PhoneNumber);
        Assert.Null(replaced.HomeLatitude);
        Assert.Null(replaced.HomePublicLatitude);
        Assert.Null(replaced.HomeDistrictId);
        Assert.Null(replaced.HomePointUpdatedAt);
        Assert.Equal(h.Clock.GetUtcNow().UtcDateTime, replaced.CreatedAt);
        Assert.False(replaced.IsEmailConfirmed);

        var tokens = await TokensAsync(db, original.Id);
        Assert.Equal(2, tokens.Count);
        Assert.NotNull(tokens[0].ConsumedAt);
        Assert.Null(tokens[1].ConsumedAt);
        Assert.Equal(2, h.Sender.CountFor("new.user@test.local"));

        // The squatter's link is dead, and the owner's still needs the OWNER's password.
        var oldLink = await h.Auth.VerifyEmailAsync(new VerifyEmailRequest { Token = oldToken, Password = "AttackerPassword1" });
        Assert.False(oldLink.IsSuccess);
        Assert.Equal("auth.verification_token_invalid", oldLink.Error!.Code);

        var newToken = h.Sender.LastTokenFor("new.user@test.local")!;
        var attackerPassword = await h.Auth.VerifyEmailAsync(new VerifyEmailRequest { Token = newToken, Password = "AttackerPassword1" });
        Assert.Equal("auth.invalid_credentials", attackerPassword.Error!.Code);
        var ownerVerifies = await h.Auth.VerifyEmailAsync(new VerifyEmailRequest { Token = newToken, Password = "OwnerPassword22" });
        Assert.True(ownerVerifies.IsSuccess);
    }

    [Fact]
    public async Task Reregister_Within_Cooldown_Is_Rejected_And_Changes_Nothing()
    {
        using var db = new SqliteTestDatabase();
        await using var context = db.CreateContext();
        var h = new AuthHarness(context);

        await h.Auth.RegisterAsync(AuthHarness.Register(password: "FirstPassword11"));
        var before = await UserAsync(db, "new.user@test.local");
        var tokensBefore = await TokensAsync(db, before.Id);

        h.Clock.Advance(TimeSpan.FromSeconds(20));
        var result = await h.Auth.RegisterAsync(AuthHarness.Register(password: "SecondPassword22", firstName: "Other"));

        Assert.False(result.IsSuccess);
        Assert.Equal("auth.verification_cooldown", result.Error!.Code);
        Assert.Equal(40, result.Error.RetryAfterSeconds);

        var after = await UserAsync(db, "new.user@test.local");
        Assert.Equal(before.PasswordHash, after.PasswordHash);
        Assert.Equal(before.FirstName, after.FirstName);
        Assert.Equal(before.CreatedAt, after.CreatedAt);
        var tokensAfter = await TokensAsync(db, before.Id);
        Assert.Equal(tokensBefore.Select(token => token.Id), tokensAfter.Select(token => token.Id));
        Assert.All(tokensAfter, token => Assert.Null(token.ConsumedAt));
        Assert.Single(h.Sender.Messages);
    }

    [Fact]
    public async Task A_Verified_Account_Is_Never_Replaced()
    {
        using var db = new SqliteTestDatabase();
        var verified = TestData.User(PendingId, Email, passwordHash: "hashed:RealPassword1");
        await db.SeedAsync(verified);
        await using var context = db.CreateContext();
        var h = new AuthHarness(context);

        var result = await h.Auth.RegisterAsync(AuthHarness.Register(Email, "AttackerPassword1"));

        Assert.False(result.IsSuccess);
        Assert.Equal("auth.duplicate_email", result.Error!.Code);
        Assert.Equal("hashed:RealPassword1", (await UserAsync(db, Email)).PasswordHash);
        Assert.Empty(await TokensAsync(db, PendingId));
        Assert.Empty(h.Sender.Messages);
    }

    [Fact]
    public async Task A_Blocked_Pending_Account_Is_Never_Replaced()
    {
        using var db = new SqliteTestDatabase();
        await db.SeedAsync(Pending(blocked: true));
        await using var context = db.CreateContext();
        var h = new AuthHarness(context);

        var result = await h.Auth.RegisterAsync(AuthHarness.Register(Email, "AttackerPassword1"));

        Assert.False(result.IsSuccess);
        Assert.Equal("auth.duplicate_email", result.Error!.Code);
        Assert.Equal($"hashed:{Password}", (await UserAsync(db, Email)).PasswordHash);
        Assert.Empty(h.Sender.Messages);
    }

    [Fact]
    public async Task Replacement_Is_Conditional_On_Pending_And_Unblocked_At_Write_Time()
    {
        // The service looked, saw "pending", and the account was verified before the write. The
        // UPDATE's predicate - not the earlier read - is what protects the verified account.
        using var db = new SqliteTestDatabase();
        await db.SeedAsync(TestData.User(PendingId, Email, passwordHash: "hashed:RealPassword1")); // verified
        await using var context = db.CreateContext();
        var store = new EmailVerificationStore(context);
        var now = DateTime.UtcNow;

        var outcome = await store.TryReplacePendingAsync(
            PendingId,
            TestData.User(Guid.NewGuid(), Email, passwordHash: "hashed:Attacker1", isEmailConfirmed: false),
            ActiveTokenFor(PendingId, "tok", now),
            now,
            Limits(now));

        Assert.Equal(ReplacePendingOutcome.NotReplaceable, outcome);
        Assert.Equal("hashed:RealPassword1", (await UserAsync(db, Email)).PasswordHash);
        Assert.Empty(await TokensAsync(db, PendingId));
    }

    // ---- Verify ----------------------------------------------------------------------------------------

    private static async Task<(AuthHarness Harness, string Token)> RegisteredAsync(SqliteTestDatabase db, AppDbContext context, IPasswordHasher? hasher = null)
    {
        var h = new AuthHarness(context, hasher);
        await h.Auth.RegisterAsync(AuthHarness.Register(Email, Password));
        return (h, h.Sender.LastTokenFor(Email)!);
    }

    [Fact]
    public async Task Verify_Succeeds_Marks_The_User_Verified_Consumes_The_Token_And_Signs_In()
    {
        using var db = new SqliteTestDatabase();
        await using var context = db.CreateContext();
        var (h, token) = await RegisteredAsync(db, context);

        h.Clock.Advance(TimeSpan.FromMinutes(5));
        var result = await h.Auth.VerifyEmailAsync(new VerifyEmailRequest { Token = token, Password = Password });

        Assert.True(result.IsSuccess);
        var user = await UserAsync(db, Email);
        Assert.StartsWith("fake-token:", result.Value!.AccessToken, StringComparison.Ordinal);
        Assert.EndsWith(user.Id.ToString(), result.Value.AccessToken, StringComparison.Ordinal);
        Assert.Equal(Email, result.Value.User.Email);
        Assert.True(user.IsEmailConfirmed);
        Assert.Equal(h.Clock.GetUtcNow().UtcDateTime, user.EmailConfirmedAt);
        var stored = Assert.Single(await TokensAsync(db, user.Id));
        Assert.Equal(h.Clock.GetUtcNow().UtcDateTime, stored.ConsumedAt);
    }

    [Fact]
    public async Task Verify_Wrong_Password_Is_Unauthorized_And_Leaves_The_Token_Intact()
    {
        using var db = new SqliteTestDatabase();
        await using var context = db.CreateContext();
        var (h, token) = await RegisteredAsync(db, context);

        var wrong = await h.Auth.VerifyEmailAsync(new VerifyEmailRequest { Token = token, Password = "WrongPassword9" });

        Assert.False(wrong.IsSuccess);
        Assert.Equal("auth.invalid_credentials", wrong.Error!.Code);
        var user = await UserAsync(db, Email);
        Assert.False(user.IsEmailConfirmed);
        Assert.Null((await TokensAsync(db, user.Id)).Single().ConsumedAt);

        // And the link still works for the right password afterwards.
        var right = await h.Auth.VerifyEmailAsync(new VerifyEmailRequest { Token = token, Password = Password });
        Assert.True(right.IsSuccess);
    }

    [Fact]
    public async Task Verify_Account_With_An_Empty_Password_Hash_Is_InvalidCredentials_Not_A_Crash()
    {
        // BCrypt.Verify THROWS on an empty hash (M-013), so this must use the real hasher: the fake
        // returns false and would hide a 500.
        using var db = new SqliteTestDatabase();
        var user = TestData.User(PendingId, Email, passwordHash: string.Empty, isEmailConfirmed: false);
        await db.SeedAsync(user);
        await using var context = db.CreateContext();
        var h = new AuthHarness(context, new BcryptPasswordHasher());
        var now = h.Clock.GetUtcNow().UtcDateTime;
        await db.SeedAsync(ActiveTokenFor(PendingId, "empty-hash-token", now));

        var result = await h.Auth.VerifyEmailAsync(new VerifyEmailRequest { Token = "empty-hash-token", Password = "AnyPassword1" });

        Assert.False(result.IsSuccess);
        Assert.Equal("auth.invalid_credentials", result.Error!.Code);
        Assert.False((await UserAsync(db, Email)).IsEmailConfirmed);
        Assert.Null((await TokensAsync(db, PendingId)).Single().ConsumedAt);
    }

    [Fact]
    public async Task Verify_Expires_Exactly_At_ExpiresAt()
    {
        using var db = new SqliteTestDatabase();
        await using var context = db.CreateContext();
        var (h, token) = await RegisteredAsync(db, context);

        h.Clock.Advance(TimeSpan.FromHours(24) - TimeSpan.FromMilliseconds(1));
        var justBefore = await h.Auth.VerifyEmailAsync(new VerifyEmailRequest { Token = token, Password = "WrongPassword9" });
        Assert.Equal("auth.invalid_credentials", justBefore.Error!.Code); // not expired yet: got as far as the password

        h.Clock.Advance(TimeSpan.FromMilliseconds(1));
        var atBoundary = await h.Auth.VerifyEmailAsync(new VerifyEmailRequest { Token = token, Password = Password });
        Assert.False(atBoundary.IsSuccess);
        Assert.Equal("auth.verification_token_expired", atBoundary.Error!.Code);
        Assert.False((await UserAsync(db, Email)).IsEmailConfirmed);
    }

    [Fact]
    public async Task Verify_Just_Before_Expiry_Succeeds()
    {
        using var db = new SqliteTestDatabase();
        await using var context = db.CreateContext();
        var (h, token) = await RegisteredAsync(db, context);

        h.Clock.Advance(TimeSpan.FromHours(24) - TimeSpan.FromMilliseconds(1));
        var result = await h.Auth.VerifyEmailAsync(new VerifyEmailRequest { Token = token, Password = Password });

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task Verify_Twice_Second_Attempt_Is_AlreadyVerified()
    {
        using var db = new SqliteTestDatabase();
        await using var context = db.CreateContext();
        var (h, token) = await RegisteredAsync(db, context);

        Assert.True((await h.Auth.VerifyEmailAsync(new VerifyEmailRequest { Token = token, Password = Password })).IsSuccess);
        var second = await h.Auth.VerifyEmailAsync(new VerifyEmailRequest { Token = token, Password = Password });

        Assert.False(second.IsSuccess);
        Assert.Equal("auth.email_already_verified", second.Error!.Code);
    }

    [Fact]
    public async Task Verify_Token_Of_Another_Purpose_Is_Invalid()
    {
        using var db = new SqliteTestDatabase();
        await db.SeedAsync(Pending());
        await using var context = db.CreateContext();
        var h = new AuthHarness(context);
        var foreign = ActiveTokenFor(PendingId, "password-reset-token", h.Clock.GetUtcNow().UtcDateTime);
        foreign.Purpose = (TokenPurpose)99;
        await db.SeedAsync(foreign);

        var result = await h.Auth.VerifyEmailAsync(new VerifyEmailRequest { Token = "password-reset-token", Password = Password });

        Assert.False(result.IsSuccess);
        Assert.Equal("auth.verification_token_invalid", result.Error!.Code);
        Assert.False((await UserAsync(db, Email)).IsEmailConfirmed);
    }

    [Fact]
    public async Task Verify_Blocked_User_Is_Refused()
    {
        using var db = new SqliteTestDatabase();
        await using var context = db.CreateContext();
        var (h, token) = await RegisteredAsync(db, context);
        await using (var admin = db.CreateContext())
        {
            await admin.Users.Where(user => user.Email == Email).ExecuteUpdateAsync(set => set.SetProperty(user => user.IsBlocked, true));
        }

        var result = await h.Auth.VerifyEmailAsync(new VerifyEmailRequest { Token = token, Password = Password });

        Assert.False(result.IsSuccess);
        Assert.Equal("auth.user_blocked", result.Error!.Code);
        Assert.False((await UserAsync(db, Email)).IsEmailConfirmed);
    }

    [Fact]
    public async Task Verify_For_An_Already_Verified_User_Is_AlreadyVerified_Even_With_An_Unused_Token()
    {
        using var db = new SqliteTestDatabase();
        await using var context = db.CreateContext();
        var (h, token) = await RegisteredAsync(db, context);
        await using (var other = db.CreateContext())
        {
            await other.Users.Where(user => user.Email == Email).ExecuteUpdateAsync(set => set.SetProperty(user => user.IsEmailConfirmed, true));
        }

        var result = await h.Auth.VerifyEmailAsync(new VerifyEmailRequest { Token = token, Password = Password });

        Assert.Equal("auth.email_already_verified", result.Error!.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no-such-token")]
    public async Task Verify_Unknown_Or_Empty_Token_Is_Invalid(string token)
    {
        using var db = new SqliteTestDatabase();
        await using var context = db.CreateContext();
        var h = new AuthHarness(context);

        var result = await h.Auth.VerifyEmailAsync(new VerifyEmailRequest { Token = token, Password = Password });

        Assert.Equal("auth.verification_token_invalid", result.Error!.Code);
    }

    [Fact]
    public async Task Verify_Commit_Rolls_Back_When_The_Password_Changed_After_It_Was_Checked()
    {
        // The store-level half of the replacement race: BCrypt passed against the OLD hash, then a
        // replacement installed a new one. The predicate PasswordHash=@seenHash must fail the commit.
        using var db = new SqliteTestDatabase();
        await using var context = db.CreateContext();
        var (h, _) = await RegisteredAsync(db, context);
        var user = await UserAsync(db, Email);
        var token = (await TokensAsync(db, user.Id)).Single();

        await using var verifyContext = db.CreateContext();
        var committed = await new EmailVerificationStore(verifyContext).TryCommitVerificationAsync(
            token.Id, user.Id, TokenPurpose.EmailVerification, "hashed:SomeOtherPassword", h.Clock.GetUtcNow().UtcDateTime);

        Assert.False(committed);
        Assert.False((await UserAsync(db, Email)).IsEmailConfirmed);
        Assert.Null((await TokensAsync(db, user.Id)).Single().ConsumedAt);
    }

    [Fact]
    public async Task Verify_Commit_Undoes_The_User_Update_When_The_Token_Update_Fails()
    {
        // Users is updated first (lock order). If the token UPDATE then affects no row (here: it
        // expired in between), the user UPDATE must be rolled back - never a verified user with a
        // live token, nor the reverse.
        using var db = new SqliteTestDatabase();
        await using var context = db.CreateContext();
        var (h, _) = await RegisteredAsync(db, context);
        var user = await UserAsync(db, Email);
        var token = (await TokensAsync(db, user.Id)).Single();

        await using var verifyContext = db.CreateContext();
        var committed = await new EmailVerificationStore(verifyContext).TryCommitVerificationAsync(
            token.Id, user.Id, TokenPurpose.EmailVerification, user.PasswordHash, token.ExpiresAt);

        Assert.False(committed);
        Assert.False((await UserAsync(db, Email)).IsEmailConfirmed);
        Assert.Null((await TokensAsync(db, user.Id)).Single().ConsumedAt);
    }

    // ---- Login ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Login_Unverified_With_The_Correct_Password_Is_EmailNotVerified()
    {
        using var db = new SqliteTestDatabase();
        await using var context = db.CreateContext();
        var (h, _) = await RegisteredAsync(db, context);

        var result = await h.Auth.LoginAsync(new LoginRequest { Email = Email, Password = Password });

        Assert.False(result.IsSuccess);
        Assert.Equal("auth.email_not_verified", result.Error!.Code);
    }

    [Fact]
    public async Task Login_Unverified_With_A_Wrong_Password_Does_Not_Reveal_That_The_Account_Exists()
    {
        using var db = new SqliteTestDatabase();
        await using var context = db.CreateContext();
        var (h, _) = await RegisteredAsync(db, context);

        var result = await h.Auth.LoginAsync(new LoginRequest { Email = Email, Password = "WrongPassword9" });

        Assert.Equal("auth.invalid_credentials", result.Error!.Code);
    }

    [Fact]
    public async Task Login_Blocked_And_Unverified_Reports_Blocked_First()
    {
        using var db = new SqliteTestDatabase();
        await db.SeedAsync(Pending(blocked: true));
        await using var context = db.CreateContext();
        var h = new AuthHarness(context);

        var result = await h.Auth.LoginAsync(new LoginRequest { Email = Email, Password = Password });

        Assert.Equal("auth.user_blocked", result.Error!.Code);
    }

    [Fact]
    public async Task Login_Works_After_Verification()
    {
        using var db = new SqliteTestDatabase();
        await using var context = db.CreateContext();
        var (h, token) = await RegisteredAsync(db, context);
        await h.Auth.VerifyEmailAsync(new VerifyEmailRequest { Token = token, Password = Password });

        var result = await h.Auth.LoginAsync(new LoginRequest { Email = Email, Password = Password });

        Assert.True(result.IsSuccess);
    }

    // ---- Resend ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Resend_For_An_Unknown_Email_Succeeds_Silently()
    {
        using var db = new SqliteTestDatabase();
        await using var context = db.CreateContext();
        var h = new AuthHarness(context);

        var result = await h.Auth.ResendVerificationAsync(new ResendVerificationRequest { Email = "nobody@test.local" });

        Assert.True(result.IsSuccess);
        Assert.Empty(h.Sender.Messages);
        Assert.Empty(await db.CreateContext().UserTokens.ToListAsync());
    }

    [Fact]
    public async Task Resend_For_A_Verified_Or_Blocked_Account_Sends_Nothing()
    {
        using var db = new SqliteTestDatabase();
        await db.SeedAsync(
            TestData.User(Guid.NewGuid(), "verified@test.local"),
            TestData.User(Guid.NewGuid(), "blocked@test.local", isBlocked: true, isEmailConfirmed: false));
        await using var context = db.CreateContext();
        var h = new AuthHarness(context);

        Assert.True((await h.Auth.ResendVerificationAsync(new ResendVerificationRequest { Email = "verified@test.local" })).IsSuccess);
        Assert.True((await h.Auth.ResendVerificationAsync(new ResendVerificationRequest { Email = "blocked@test.local" })).IsSuccess);

        Assert.Empty(h.Sender.Messages);
        Assert.Empty(await db.CreateContext().UserTokens.ToListAsync());
    }

    [Fact]
    public async Task Resend_Inside_The_Cooldown_Issues_Nothing_New()
    {
        using var db = new SqliteTestDatabase();
        await using var context = db.CreateContext();
        var (h, _) = await RegisteredAsync(db, context);

        h.Clock.Advance(TimeSpan.FromSeconds(30));
        var result = await h.Auth.ResendVerificationAsync(new ResendVerificationRequest { Email = Email });

        Assert.True(result.IsSuccess);
        Assert.Single(h.Sender.Messages);
        var token = Assert.Single(await db.CreateContext().UserTokens.AsNoTracking().ToListAsync());
        Assert.Null(token.ConsumedAt);
    }

    [Fact]
    public async Task Resend_After_The_Cooldown_Revokes_The_Old_Link_And_Sends_A_New_One()
    {
        using var db = new SqliteTestDatabase();
        await using var context = db.CreateContext();
        var (h, oldToken) = await RegisteredAsync(db, context);

        h.Clock.Advance(TimeSpan.FromSeconds(61));
        var result = await h.Auth.ResendVerificationAsync(new ResendVerificationRequest { Email = Email });

        Assert.True(result.IsSuccess);
        Assert.Equal(2, h.Sender.CountFor(Email));
        var newToken = h.Sender.LastTokenFor(Email)!;
        Assert.NotEqual(oldToken, newToken);

        var user = await UserAsync(db, Email);
        var tokens = await TokensAsync(db, user.Id);
        Assert.Equal(2, tokens.Count);
        Assert.Equal(1, tokens.Count(token => token.ConsumedAt is null)); // one active token, always

        var old = await h.Auth.VerifyEmailAsync(new VerifyEmailRequest { Token = oldToken, Password = Password });
        Assert.Equal("auth.verification_token_invalid", old.Error!.Code);
        Assert.True((await h.Auth.VerifyEmailAsync(new VerifyEmailRequest { Token = newToken, Password = Password })).IsSuccess);
    }

    [Fact]
    public async Task Resend_Is_Capped_At_Five_Tokens_Per_24_Hours_And_Recovers_After_The_Window()
    {
        using var db = new SqliteTestDatabase();
        await using var context = db.CreateContext();
        var (h, _) = await RegisteredAsync(db, context); // token 1

        for (var i = 0; i < 4; i++) // tokens 2..5
        {
            h.Clock.Advance(TimeSpan.FromSeconds(61));
            await h.Auth.ResendVerificationAsync(new ResendVerificationRequest { Email = Email });
        }

        Assert.Equal(5, h.Sender.CountFor(Email));

        h.Clock.Advance(TimeSpan.FromSeconds(61));
        var sixth = await h.Auth.ResendVerificationAsync(new ResendVerificationRequest { Email = Email });

        Assert.True(sixth.IsSuccess); // always 202 to the caller
        Assert.Equal(5, h.Sender.CountFor(Email));
        Assert.Equal(5, (await db.CreateContext().UserTokens.CountAsync()));

        h.Clock.Advance(TimeSpan.FromHours(24));
        await h.Auth.ResendVerificationAsync(new ResendVerificationRequest { Email = Email });
        Assert.Equal(6, h.Sender.CountFor(Email));
    }

    [Fact]
    public async Task Resend_With_An_Exhausted_Global_Budget_Sends_Nothing_And_Still_Succeeds()
    {
        using var db = new SqliteTestDatabase();
        await using var context = db.CreateContext();
        var (h, _) = await RegisteredAsync(db, context);
        h.Budget.Remaining = 0;

        h.Clock.Advance(TimeSpan.FromSeconds(61));
        var result = await h.Auth.ResendVerificationAsync(new ResendVerificationRequest { Email = Email });

        Assert.True(result.IsSuccess);
        Assert.Single(h.Sender.Messages); // only the registration email
    }

    [Fact]
    public async Task Resend_Gate_Closed_Returns_Unavailable()
    {
        using var db = new SqliteTestDatabase();
        await db.SeedAsync(Pending());
        await using var context = db.CreateContext();
        var h = new AuthHarness(context);
        h.Settings.IsOperational = false;

        var result = await h.Auth.ResendVerificationAsync(new ResendVerificationRequest { Email = Email });

        Assert.False(result.IsSuccess);
        Assert.Equal("auth.registration_unavailable", result.Error!.Code);
        Assert.Empty(await db.CreateContext().UserTokens.ToListAsync());
    }

    [Fact]
    public async Task Rotating_Inside_The_Cooldown_Is_A_Conflict_That_Changes_Nothing()
    {
        // The database, not the service's earlier read, enforces "one active token": a younger
        // active token makes the filtered unique index reject the insert.
        using var db = new SqliteTestDatabase();
        await db.SeedAsync(Pending());
        var now = DateTime.UtcNow;
        await db.SeedAsync(ActiveTokenFor(PendingId, "first", now.AddSeconds(-10)));
        await using var context = db.CreateContext();
        var store = new EmailVerificationStore(context);

        var outcome = await store.TryRotateTokenAsync(ActiveTokenFor(PendingId, "second", now), now, Limits(now));

        Assert.Equal(RotateTokenOutcome.Conflict, outcome);
        var token = Assert.Single(await TokensAsync(db, PendingId));
        Assert.Null(token.ConsumedAt);
        Assert.Empty(context.ChangeTracker.Entries());
    }

    // ---- The per-recipient cap covers re-registration too (ADR-028 amendment 2026-10-09) ----

    [Fact]
    public async Task The_Sixth_Re_registration_In_24_Hours_Is_Refused_And_Changes_Nothing()
    {
        using var db = new SqliteTestDatabase();
        await using var context = db.CreateContext();
        var h = new AuthHarness(context);

        await h.Auth.RegisterAsync(AuthHarness.Register(password: "Password00000")); // token 1
        for (var i = 1; i < 5; i++) // tokens 2..5, all by re-registration
        {
            h.Clock.Advance(TimeSpan.FromSeconds(61));
            Assert.True((await h.Auth.RegisterAsync(AuthHarness.Register(password: $"Password{i}0000", firstName: $"Name{i}"))).IsSuccess);
        }

        var before = await UserAsync(db, "new.user@test.local");
        var emailsBefore = h.Sender.Messages.Count;
        h.Clock.Advance(TimeSpan.FromSeconds(61));

        var sixth = await h.Auth.RegisterAsync(AuthHarness.Register(password: "Overwritten9999", firstName: "Overwritten"));

        Assert.False(sixth.IsSuccess);
        Assert.Equal("auth.verification_cooldown", sixth.Error!.Code);
        // The daily cap reports the REAL wait: the first token (created 4 x 61 s + 61 s ago) leaves
        // the 24 h window at firstCreatedAt + 24 h, i.e. 24 h minus 5 x 61 s from now. Never 60.
        Assert.Equal((int)(TimeSpan.FromHours(24) - TimeSpan.FromSeconds(5 * 61)).TotalSeconds, sixth.Error.RetryAfterSeconds);

        // The over-cap case is signalled for monitoring, by user id.
        Assert.Equal(before.Id, Assert.Single(h.Monitor.CapReached));
        var after = await UserAsync(db, "new.user@test.local");
        Assert.Equal(before.PasswordHash, after.PasswordHash);
        Assert.Equal(before.FirstName, after.FirstName);
        Assert.Equal(before.CreatedAt, after.CreatedAt);
        Assert.Equal(5, await db.CreateContext().UserTokens.CountAsync());
        Assert.Equal(emailsBefore, h.Sender.Messages.Count);

        // The window rolls: 24 h after the first token, registration works again.
        h.Clock.Advance(TimeSpan.FromHours(24));
        Assert.True((await h.Auth.RegisterAsync(AuthHarness.Register(password: "Password60000"))).IsSuccess);
    }

    [Fact]
    public async Task The_Plain_Cooldown_Still_Reports_The_Remaining_Cooldown_Not_The_Daily_Window()
    {
        using var db = new SqliteTestDatabase();
        await using var context = db.CreateContext();
        var h = new AuthHarness(context);
        await h.Auth.RegisterAsync(AuthHarness.Register());
        h.Clock.Advance(TimeSpan.FromSeconds(25));

        var again = await h.Auth.RegisterAsync(AuthHarness.Register());

        Assert.Equal("auth.verification_cooldown", again.Error!.Code);
        Assert.Equal(35, again.Error.RetryAfterSeconds);
        Assert.Empty(h.Monitor.CapReached); // a plain cooldown is NOT a cap event
    }

    [Fact]
    public async Task Resend_And_Re_registration_Count_Against_The_Same_Cap()
    {
        using var db = new SqliteTestDatabase();
        await using var context = db.CreateContext();
        var h = new AuthHarness(context);

        await h.Auth.RegisterAsync(AuthHarness.Register()); // 1
        h.Clock.Advance(TimeSpan.FromSeconds(61));
        await h.Auth.ResendVerificationAsync(new ResendVerificationRequest { Email = "new.user@test.local" }); // 2
        h.Clock.Advance(TimeSpan.FromSeconds(61));
        await h.Auth.RegisterAsync(AuthHarness.Register()); // 3
        h.Clock.Advance(TimeSpan.FromSeconds(61));
        await h.Auth.ResendVerificationAsync(new ResendVerificationRequest { Email = "new.user@test.local" }); // 4
        h.Clock.Advance(TimeSpan.FromSeconds(61));
        await h.Auth.RegisterAsync(AuthHarness.Register()); // 5
        Assert.Equal(5, h.Sender.Messages.Count);

        h.Clock.Advance(TimeSpan.FromSeconds(61));
        var overCap = await h.Auth.RegisterAsync(AuthHarness.Register(password: "Overwritten9999"));
        await h.Auth.ResendVerificationAsync(new ResendVerificationRequest { Email = "new.user@test.local" });

        Assert.Equal("auth.verification_cooldown", overCap.Error!.Code);
        Assert.Equal(5, h.Sender.Messages.Count);
    }

    [Fact]
    public async Task The_Store_Enforces_The_Cap_Itself_Inside_The_Transaction()
    {
        // The service pre-checks the cap; this proves the transaction does too, which is what makes
        // it race-free against concurrent writers for the same user.
        using var db = new SqliteTestDatabase();
        await db.SeedAsync(Pending());
        var now = DateTime.UtcNow;
        for (var i = 0; i < 5; i++)
        {
            var consumed = ActiveTokenFor(PendingId, $"old-{i}", now.AddHours(-2).AddMinutes(i));
            consumed.ConsumedAt = now.AddHours(-1);
            await db.SeedAsync(consumed);
        }

        await using var context = db.CreateContext();
        var store = new EmailVerificationStore(context);

        var replace = await store.TryReplacePendingAsync(
            PendingId,
            TestData.User(Guid.NewGuid(), Email, passwordHash: "hashed:Attacker1", isEmailConfirmed: false),
            ActiveTokenFor(PendingId, "new", now), now, Limits(now));
        var rotate = await store.TryRotateTokenAsync(ActiveTokenFor(PendingId, "new2", now), now, Limits(now));

        Assert.Equal(ReplacePendingOutcome.OverCap, replace);
        Assert.Equal(RotateTokenOutcome.Conflict, rotate);
        Assert.Equal($"hashed:{Password}", (await UserAsync(db, Email)).PasswordHash);
        Assert.Equal(5, (await TokensAsync(db, PendingId)).Count);
    }

    [Fact]
    public async Task Replacement_Keeps_A_Fresh_Concurrent_Token_So_The_Insert_Is_Rejected_And_Everything_Rolls_Back()
    {
        using var db = new SqliteTestDatabase();
        await db.SeedAsync(Pending());
        var now = DateTime.UtcNow;
        await db.SeedAsync(ActiveTokenFor(PendingId, "fresh", now.AddSeconds(-5)));
        await using var context = db.CreateContext();

        var outcome = await new EmailVerificationStore(context).TryReplacePendingAsync(
            PendingId,
            TestData.User(Guid.NewGuid(), Email, passwordHash: "hashed:Attacker1", isEmailConfirmed: false),
            ActiveTokenFor(PendingId, "new", now), now, Limits(now));

        Assert.Equal(ReplacePendingOutcome.TokenConflict, outcome);
        Assert.Equal($"hashed:{Password}", (await UserAsync(db, Email)).PasswordHash); // the Users update rolled back too
        var token = Assert.Single(await TokensAsync(db, PendingId));
        Assert.Null(token.ConsumedAt);
    }

    // ---- The home point never lands on an account somebody else now owns ----

    private sealed class ResetAfterReplaceStore : IEmailVerificationStore
    {
        private readonly IEmailVerificationStore _inner;
        private readonly Func<Task> _afterReplace;

        public ResetAfterReplaceStore(IEmailVerificationStore inner, Func<Task> afterReplace)
        {
            _inner = inner;
            _afterReplace = afterReplace;
        }

        public async Task<ReplacePendingOutcome> TryReplacePendingAsync(Guid userId, User candidate, UserToken token, DateTime now, TokenLimits limits, CancellationToken cancellationToken = default)
        {
            var outcome = await _inner.TryReplacePendingAsync(userId, candidate, token, now, limits, cancellationToken);
            await _afterReplace();
            return outcome;
        }

        public Task<AccountState?> FindAccountStateAsync(string email, CancellationToken cancellationToken = default) => _inner.FindAccountStateAsync(email, cancellationToken);
        public Task<DateTime?> GetLatestTokenCreatedAtAsync(Guid userId, TokenPurpose purpose, CancellationToken cancellationToken = default) => _inner.GetLatestTokenCreatedAtAsync(userId, purpose, cancellationToken);
        public Task<DateTime?> GetOldestTokenCreatedAtSinceAsync(Guid userId, TokenPurpose purpose, DateTime since, CancellationToken cancellationToken = default) => _inner.GetOldestTokenCreatedAtSinceAsync(userId, purpose, since, cancellationToken);
        public Task<int> CountTokensCreatedSinceAsync(Guid userId, TokenPurpose purpose, DateTime since, CancellationToken cancellationToken = default) => _inner.CountTokensCreatedSinceAsync(userId, purpose, since, cancellationToken);
        public Task<bool> TryAddUserAsync(User user, UserToken? token, CancellationToken cancellationToken = default) => _inner.TryAddUserAsync(user, token, cancellationToken);
        public Task<RotateTokenOutcome> TryRotateTokenAsync(UserToken token, DateTime now, TokenLimits limits, CancellationToken cancellationToken = default) => _inner.TryRotateTokenAsync(token, now, limits, cancellationToken);
        public Task<VerificationTokenView?> FindTokenAsync(byte[] tokenHash, TokenPurpose purpose, CancellationToken cancellationToken = default) => _inner.FindTokenAsync(tokenHash, purpose, cancellationToken);
        public Task<bool> TryCommitVerificationAsync(Guid tokenId, Guid userId, TokenPurpose purpose, string seenPasswordHash, DateTime now, CancellationToken cancellationToken = default) => _inner.TryCommitVerificationAsync(tokenId, userId, purpose, seenPasswordHash, now, cancellationToken);
        public Task<bool> TryResetPendingForExternalAsync(Guid userId, ExternalUserInfo external, string firstName, string lastName, DateTime now, CancellationToken cancellationToken = default) => _inner.TryResetPendingForExternalAsync(userId, external, firstName, lastName, now, cancellationToken);
        public Task<int> DiscardHomePointIfAccountChangedAsync(Guid userId, string registrantPasswordHash, CancellationToken cancellationToken = default) => _inner.DiscardHomePointIfAccountChangedAsync(userId, registrantPasswordHash, cancellationToken);
    }

    [Fact]
    public async Task A_Home_Point_Is_Not_Left_On_An_Account_An_External_Sign_In_Took_Over_Mid_Registration()
    {
        using var db = new SqliteTestDatabase();
        await db.SeedAsync(Pending("hijack@example.org"));
        await using var context = db.CreateContext();
        var (latitude, longitude) = TestData.KentronPoint;

        // The external sign-in resets and verifies the account right after the replacement commits,
        // before the registrant home point is written.
        await using var externalContext = db.CreateContext();
        var external = new AuthHarness(externalContext);
        external.External.Result = Google("hijack@example.org");
        var h = new AuthHarness(
            context,
            decorateStore: inner => new ResetAfterReplaceStore(inner, async () =>
                Assert.True((await external.Auth.ExternalAsync(AnyExternalRequest)).IsSuccess)));
        h.Clock.Advance(TimeSpan.FromMinutes(5));

        await h.Auth.RegisterAsync(AuthHarness.Register("hijack@example.org", latitude: latitude, longitude: longitude));

        var user = await UserAsync(db, "hijack@example.org");
        Assert.True(user.IsEmailConfirmed);
        Assert.Equal("google", user.ExternalAuthProvider);
        Assert.Null(user.HomeLatitude);
        Assert.Null(user.HomeDistrictId);
    }

    [Fact]
    public async Task The_Registrants_Own_Home_Point_Survives_The_Compensation()
    {
        using var db = new SqliteTestDatabase();
        await using var context = db.CreateContext();
        var h = new AuthHarness(context);
        var (latitude, longitude) = TestData.KentronPoint;

        await h.Auth.RegisterAsync(AuthHarness.Register(latitude: latitude, longitude: longitude));

        Assert.Equal(latitude, (await UserAsync(db, "new.user@test.local")).HomeLatitude);
    }

    // ---- External sign-in ---------------------------------------------------------------------------------

    private static ExternalUserInfo Google(string email, string? hostedDomain = null, string sub = "google-sub-1") => new()
    {
        Provider = "google",
        ProviderUserId = sub,
        Email = email,
        FirstName = "Gina",
        LastName = "Google",
        HostedDomain = hostedDomain
    };

    private static ExternalUserInfo Apple(string email, string sub = "apple-sub-1") => new()
    {
        Provider = "apple",
        ProviderUserId = sub,
        Email = email,
        FirstName = "Alex",
        LastName = "Apple"
    };

    private static readonly ExternalAuthRequest AnyExternalRequest = new() { Provider = "x", IdToken = "y" };

    [Fact]
    public async Task External_New_User_Is_Created_Verified_With_EmailConfirmedAt()
    {
        using var db = new SqliteTestDatabase();
        await using var context = db.CreateContext();
        var h = new AuthHarness(context);
        h.External.Result = Google("fresh@example.org");

        var result = await h.Auth.ExternalAsync(AnyExternalRequest);

        Assert.True(result.IsSuccess);
        var user = await UserAsync(db, "fresh@example.org");
        Assert.True(user.IsEmailConfirmed);
        Assert.Equal(h.Clock.GetUtcNow().UtcDateTime, user.EmailConfirmedAt);
        Assert.Empty(h.Sender.Messages);
    }

    [Theory]
    [InlineData("google", "someone@gmail.com", null, true)]
    [InlineData("google", "someone@googlemail.com", null, true)]
    [InlineData("google", "Someone@GMAIL.com", null, true)]
    [InlineData("google", "someone@example.org", "example.org", true)]
    [InlineData("google", "someone@example.org", "EXAMPLE.org", true)]
    [InlineData("google", "someone@example.org", "other.org", false)]
    [InlineData("google", "someone@example.org", null, false)]
    [InlineData("google", "someone@mail.example.org", "example.org", false)]
    [InlineData("apple", "someone@privaterelay.appleid.com", null, true)]
    [InlineData("apple", "someone@icloud.com", null, true)]
    [InlineData("apple", "someone@me.com", null, true)]
    [InlineData("apple", "someone@mac.com", null, true)]
    [InlineData("apple", "someone@gmail.com", null, false)]
    [InlineData("apple", "someone@example.org", null, false)]
    public async Task External_Auto_Link_To_A_Verified_Account_Follows_The_Domain_Rules(
        string provider, string email, string? hostedDomain, bool linked)
    {
        using var db = new SqliteTestDatabase();
        var existing = TestData.User(PendingId, email.ToLowerInvariant(), passwordHash: "hashed:RealPassword1");
        await db.SeedAsync(existing);
        await using var context = db.CreateContext();
        var h = new AuthHarness(context);
        h.External.Result = provider == "google" ? Google(email, hostedDomain) : Apple(email);

        var result = await h.Auth.ExternalAsync(AnyExternalRequest);

        var stored = await UserAsync(db, email.ToLowerInvariant());
        if (linked)
        {
            Assert.True(result.IsSuccess);
            Assert.Equal(provider, stored.ExternalAuthProvider);
            Assert.NotNull(stored.ExternalProviderId);
        }
        else
        {
            Assert.False(result.IsSuccess);
            Assert.Equal("auth.external_link_conflict", result.Error!.Code);
            Assert.Null(stored.ExternalAuthProvider);
            Assert.Null(stored.ExternalProviderId);
        }

        Assert.Equal("hashed:RealPassword1", stored.PasswordHash); // never touched either way
    }

    [Fact]
    public async Task External_On_A_Pending_Email_Resets_The_Registration_And_Revokes_Its_Tokens()
    {
        using var db = new SqliteTestDatabase();
        await using var context = db.CreateContext();
        var h = new AuthHarness(context);
        var (latitude, longitude) = TestData.KentronPoint;
        await h.Auth.RegisterAsync(AuthHarness.Register(
            "squatted@example.org", "AttackerPassword1", firstName: "Squatter", phone: "+374 99 000111",
            latitude: latitude, longitude: longitude));
        h.Clock.Advance(TimeSpan.FromMinutes(5));
        // An unrelated domain: pending accounts are replaced by any provider-verified proof of the mailbox.
        h.External.Result = Google("squatted@example.org");

        var result = await h.Auth.ExternalAsync(AnyExternalRequest);

        Assert.True(result.IsSuccess);
        var user = await UserAsync(db, "squatted@example.org");
        Assert.True(user.IsEmailConfirmed);
        Assert.Equal(h.Clock.GetUtcNow().UtcDateTime, user.EmailConfirmedAt);
        Assert.Equal(string.Empty, user.PasswordHash);
        Assert.Equal("Gina", user.FirstName);
        Assert.Equal("Google", user.LastName);
        Assert.Null(user.PhoneNumber);
        Assert.Null(user.HomeLatitude);
        Assert.Equal("google", user.ExternalAuthProvider);
        Assert.Equal("google-sub-1", user.ExternalProviderId);
        Assert.All(await TokensAsync(db, user.Id), token => Assert.NotNull(token.ConsumedAt));

        // The squatter's password no longer logs in, and their emailed link is dead.
        var login = await h.Auth.LoginAsync(new LoginRequest { Email = "squatted@example.org", Password = "AttackerPassword1" });
        Assert.Equal("auth.invalid_credentials", login.Error!.Code);
        var link = await h.Auth.VerifyEmailAsync(new VerifyEmailRequest
        {
            Token = h.Sender.LastTokenFor("squatted@example.org")!,
            Password = "AttackerPassword1"
        });
        Assert.False(link.IsSuccess);
    }

    [Fact]
    public async Task External_On_A_Blocked_Pending_Email_Is_Refused_And_Changes_Nothing()
    {
        using var db = new SqliteTestDatabase();
        await db.SeedAsync(Pending(blocked: true));
        await using var context = db.CreateContext();
        var h = new AuthHarness(context);
        h.External.Result = Google(Email);

        var result = await h.Auth.ExternalAsync(AnyExternalRequest);

        Assert.False(result.IsSuccess);
        Assert.Equal("auth.user_blocked", result.Error!.Code);
        var stored = await UserAsync(db, Email);
        Assert.False(stored.IsEmailConfirmed);
        Assert.Equal($"hashed:{Password}", stored.PasswordHash);
        Assert.Null(stored.ExternalProviderId);
    }

    [Fact]
    public async Task External_Losing_The_Insert_Race_Re_Runs_The_Lookup_Instead_Of_Failing()
    {
        using var db = new SqliteTestDatabase();
        await using var context = db.CreateContext();
        var h = new AuthHarness(
            context,
            decorateUserStore: inner => new RacingUserAuthStore(inner, () => db.SeedAsync(
                TestData.User(PendingId, "racer@gmail.com", passwordHash: "hashed:RealPassword1"))));
        h.External.Result = Google("racer@gmail.com");

        var result = await h.Auth.ExternalAsync(AnyExternalRequest);

        // The rival account won the unique index on Users.Email; the retry finds it, and gmail.com
        // is a domain Google may auto-link.
        Assert.True(result.IsSuccess);
        var stored = await UserAsync(db, "racer@gmail.com");
        Assert.Equal(PendingId, stored.Id);
        Assert.Equal("google", stored.ExternalAuthProvider);
        Assert.Equal(1, await db.CreateContext().Users.CountAsync());
    }

    [Fact]
    public async Task External_Sign_In_By_Provider_Id_Does_Not_Need_The_Email_Rules()
    {
        using var db = new SqliteTestDatabase();
        var linked = TestData.User(PendingId, "linked@example.org");
        linked.ExternalAuthProvider = "google";
        linked.ExternalProviderId = "google-sub-1";
        await db.SeedAsync(linked);
        await using var context = db.CreateContext();
        var h = new AuthHarness(context);
        h.External.Result = Google("linked@example.org"); // not gmail, no hd: irrelevant once linked

        var result = await h.Auth.ExternalAsync(AnyExternalRequest);

        Assert.True(result.IsSuccess);
    }
}
