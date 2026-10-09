using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using RentalPlatform.Application.DTOs;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Infrastructure;

// The races ADR-028 section 4 exists for, against a REAL SQL Server: concurrent writers (SQLite
// serialises commands on its single connection) and real lock behaviour. Each racer owns its own
// DbContext/connection and is released through a gate so the writes genuinely overlap.
//
// [SqlServerFact]: SKIPPED with a reason when no local SQL Server is reachable, and FAILED (never
// skipped) when RENTALPLATFORM_TEST_SQLSERVER_CONNECTION_STRING declares one that is down.
public sealed class EmailVerificationConcurrencyTests
{
    private const string Email = "race.user@test.local";
    private const string Password = "Sufficient1Password";
    private static readonly Guid UserId = new("e2000000-0000-0000-0000-000000000001");

    private static UserToken TokenFor(Guid userId, string raw, DateTime createdAt) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        Purpose = TokenPurpose.EmailVerification,
        TokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(raw)),
        CreatedAt = createdAt,
        ExpiresAt = createdAt.AddHours(24)
    };

    private static async Task SeedPendingAsync(SqlServerTestDatabase db, string rawToken, DateTime tokenCreatedAt)
    {
        await db.SeedAsync(
            TestData.User(UserId, Email, passwordHash: $"hashed:{Password}", isEmailConfirmed: false),
            TokenFor(UserId, rawToken, tokenCreatedAt));
    }

    private static async Task<T[]> RaceAsync<T>(int count, Func<int, Task<T>> action)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var racers = Enumerable.Range(0, count).Select(i => Task.Run(async () =>
        {
            await gate.Task;
            return await action(i);
        })).ToArray();

        await Task.Delay(100); // let every racer reach the gate
        gate.SetResult();
        return await Task.WhenAll(racers);
    }

    [SqlServerFact]
    public async Task Concurrent_Verify_Of_One_Link_Succeeds_Exactly_Once()
    {
        using var db = new SqlServerTestDatabase();
        var clock = new FakeTimeProvider(new DateTimeOffset(DateTime.UtcNow, TimeSpan.Zero));
        await SeedPendingAsync(db, "race-token", clock.GetUtcNow().UtcDateTime);

        var results = await RaceAsync(8, async _ =>
        {
            await using var context = db.CreateContext();
            var harness = new AuthHarness(context, clock: clock);
            return await harness.Auth.VerifyEmailAsync(new VerifyEmailRequest { Token = "race-token", Password = Password });
        });

        Assert.Equal(1, results.Count(result => result.IsSuccess));
        Assert.All(
            results.Where(result => !result.IsSuccess),
            result => Assert.Contains(result.Error!.Code, new[] { "auth.verification_token_invalid", "auth.email_already_verified" }));

        await using var verify = db.CreateContext();
        Assert.True((await verify.Users.AsNoTracking().SingleAsync(user => user.Id == UserId)).IsEmailConfirmed);
        Assert.NotNull((await verify.UserTokens.AsNoTracking().SingleAsync()).ConsumedAt);
    }

    // NOTE on what this proves: the verify rolls back here because the replacement also REVOKES the
    // old token, so the token UPDATE affects no row. It would pass even without the
    // PasswordHash=@seenHash predicate. That predicate is proven on its own by the store-level test
    // EmailVerificationFlowTests.Verify_Commit_Rolls_Back_When_The_Password_Changed_After_It_Was_Checked.
    [SqlServerFact]
    public async Task A_Replacement_Landing_Between_The_Password_Check_And_The_Commit_Rolls_The_Verify_Back()
    {
        using var db = new SqlServerTestDatabase();
        var clock = new FakeTimeProvider(new DateTimeOffset(DateTime.UtcNow, TimeSpan.Zero));
        // Old enough that a re-registration is past the cooldown.
        await SeedPendingAsync(db, "victim-token", clock.GetUtcNow().UtcDateTime.AddMinutes(-5));

        var hasher = new HookedPasswordHasher(new FakePasswordHasher());
        await using var verifyContext = db.CreateContext();
        var verifier = new AuthHarness(verifyContext, hasher, clock);

        // After the owner's password check passes, an attacker re-registers the same pending email.
        hasher.RunOnceAfterVerify(() => Task.Run(async () =>
        {
            await using var attackerContext = db.CreateContext();
            var attacker = new AuthHarness(attackerContext, clock: clock);
            var replaced = await attacker.Auth.RegisterAsync(AuthHarness.Register(Email, "AttackerPassword1"));
            Assert.True(replaced.IsSuccess, replaced.Error?.Code);
        }).GetAwaiter().GetResult());

        var result = await verifier.Auth.VerifyEmailAsync(new VerifyEmailRequest { Token = "victim-token", Password = Password });

        Assert.False(result.IsSuccess);
        Assert.Equal("auth.verification_token_invalid", result.Error!.Code);

        // The attacker's pending registration survives UNVERIFIED; nothing was verified with their password.
        await using var verify = db.CreateContext();
        var user = await verify.Users.AsNoTracking().SingleAsync(candidate => candidate.Id == UserId);
        Assert.False(user.IsEmailConfirmed);
        Assert.Equal("hashed:AttackerPassword1", user.PasswordHash);
        var tokens = await verify.UserTokens.AsNoTracking().Where(token => token.UserId == UserId).ToListAsync();
        Assert.Equal(2, tokens.Count);
        Assert.Equal(1, tokens.Count(token => token.ConsumedAt is null)); // only the attacker's new link is live
    }

    [SqlServerFact]
    public async Task Concurrent_Resends_Leave_Exactly_One_Active_Token_And_Send_One_Email()
    {
        using var db = new SqlServerTestDatabase();
        var clock = new FakeTimeProvider(new DateTimeOffset(DateTime.UtcNow, TimeSpan.Zero));
        await SeedPendingAsync(db, "old-token", clock.GetUtcNow().UtcDateTime.AddMinutes(-5));
        var sender = new CaptureEmailSender();

        var results = await RaceAsync(8, async _ =>
        {
            await using var context = db.CreateContext();
            var harness = new AuthHarness(context, clock: clock, sender: sender);
            return await harness.Auth.ResendVerificationAsync(new ResendVerificationRequest { Email = Email });
        });

        Assert.All(results, result => Assert.True(result.IsSuccess)); // always 202 to the caller
        Assert.Single(sender.Messages); // exactly one racer won the rotation

        await using var verify = db.CreateContext();
        var tokens = await verify.UserTokens.AsNoTracking().ToListAsync();
        Assert.Equal(2, tokens.Count);
        Assert.Equal(1, tokens.Count(token => token.ConsumedAt is null));
    }

    // Regression for a deadlock found while writing these tests: FindTokenAsync joined UserTokens to
    // Users (S on the token, then S on the user) against writers that lock Users first, and a resend's
    // token INSERT takes a shared lock on the user row for the FK check. Every writer now takes
    // Users first and the reader holds one lock at a time. Rounds are repeated because a lock-order
    // bug only shows when the interleaving lines up; any exception (1205 above all) fails the test.
    [SqlServerFact]
    public async Task Verify_Resend_And_Replacement_Racing_On_One_Account_Never_Deadlock_Or_Verify_The_Wrong_Password()
    {
        using var db = new SqlServerTestDatabase();
        var clock = new FakeTimeProvider(new DateTimeOffset(DateTime.UtcNow, TimeSpan.Zero));

        for (var round = 0; round < 25; round++)
        {
            var userId = Guid.NewGuid();
            var email = $"stress{round}@test.local";
            var raw = $"stress-token-{round}";
            await db.SeedAsync(
                TestData.User(userId, email, passwordHash: $"hashed:{Password}", isEmailConfirmed: false),
                TokenFor(userId, raw, clock.GetUtcNow().UtcDateTime.AddMinutes(-5)));

            await RaceAsync(3, async i =>
            {
                await using var context = db.CreateContext();
                var harness = new AuthHarness(context, clock: clock);
                switch (i)
                {
                    case 0:
                        return (await harness.Auth.VerifyEmailAsync(new VerifyEmailRequest { Token = raw, Password = Password })).IsSuccess;
                    case 1:
                        return (await harness.Auth.ResendVerificationAsync(new ResendVerificationRequest { Email = email })).IsSuccess;
                    default:
                        return (await harness.Auth.RegisterAsync(AuthHarness.Register(email, "AttackerPassword1"))).IsSuccess;
                }
            });

            await using var verify = db.CreateContext();
            var user = await verify.Users.AsNoTracking().SingleAsync(candidate => candidate.Id == userId);
            var active = await verify.UserTokens.AsNoTracking().CountAsync(token => token.UserId == userId && token.ConsumedAt == null);

            Assert.True(active <= 1, $"round {round}: {active} active tokens");
            if (user.IsEmailConfirmed)
            {
                // Verified means the OWNER's password check and commit won; never the attacker's hash.
                Assert.Equal($"hashed:{Password}", user.PasswordHash);
            }
        }
    }

    [SqlServerFact]
    public async Task Two_Concurrent_Re_registrations_Give_One_Created_One_429_One_Email_And_The_Winners_Password()
    {
        using var db = new SqlServerTestDatabase();
        var clock = new FakeTimeProvider(new DateTimeOffset(DateTime.UtcNow, TimeSpan.Zero));

        for (var round = 0; round < 5; round++)
        {
            var userId = Guid.NewGuid();
            var email = $"replace{round}@test.local";
            var sender = new CaptureEmailSender();
            await db.SeedAsync(
                TestData.User(userId, email, passwordHash: $"hashed:{Password}", isEmailConfirmed: false),
                TokenFor(userId, $"old-{round}", clock.GetUtcNow().UtcDateTime.AddMinutes(-5)));

            var results = await RaceAsync(2, async i =>
            {
                await using var context = db.CreateContext();
                var harness = new AuthHarness(context, clock: clock, sender: sender);
                return await harness.Auth.RegisterAsync(AuthHarness.Register(email, $"Contender{i}Password"));
            });

            Assert.Equal(1, results.Count(result => result.IsSuccess));
            var loser = Assert.Single(results, result => !result.IsSuccess);
            Assert.Equal("auth.verification_cooldown", loser.Error!.Code);
            Assert.Single(sender.Messages);

            // Exactly one password survives: the winner's. The loser's replacement rolled back whole.
            var winnerIndex = Array.FindIndex(results, result => result.IsSuccess);
            await using var verify = db.CreateContext();
            var user = await verify.Users.AsNoTracking().SingleAsync(candidate => candidate.Id == userId);
            Assert.Equal($"hashed:Contender{winnerIndex}Password", user.PasswordHash);
            Assert.Equal(1, await verify.UserTokens.CountAsync(token => token.UserId == userId && token.ConsumedAt == null));
        }
    }

    [SqlServerFact]
    public async Task A_Re_registration_Racing_A_Resend_Sends_Exactly_One_Email()
    {
        using var db = new SqlServerTestDatabase();
        var clock = new FakeTimeProvider(new DateTimeOffset(DateTime.UtcNow, TimeSpan.Zero));

        for (var round = 0; round < 5; round++)
        {
            var userId = Guid.NewGuid();
            var email = $"mixed{round}@test.local";
            var sender = new CaptureEmailSender();
            await db.SeedAsync(
                TestData.User(userId, email, passwordHash: $"hashed:{Password}", isEmailConfirmed: false),
                TokenFor(userId, $"old-{round}", clock.GetUtcNow().UtcDateTime.AddMinutes(-5)));

            var results = await RaceAsync(2, async i =>
            {
                await using var context = db.CreateContext();
                var harness = new AuthHarness(context, clock: clock, sender: sender);
                return i == 0
                    ? (await harness.Auth.RegisterAsync(AuthHarness.Register(email, "ReplacerPassword1"))).IsSuccess
                    : (await harness.Auth.ResendVerificationAsync(new ResendVerificationRequest { Email = email })).IsSuccess;
            });

            Assert.True(results[1]); // resend is always 202
            Assert.Single(sender.Messages);

            await using var verify = db.CreateContext();
            var user = await verify.Users.AsNoTracking().SingleAsync(candidate => candidate.Id == userId);
            // The password changed if and only if the re-registration was the one that sent.
            Assert.Equal(results[0] ? "hashed:ReplacerPassword1" : $"hashed:{Password}", user.PasswordHash);
            Assert.Equal(1, await verify.UserTokens.CountAsync(token => token.UserId == userId && token.ConsumedAt == null));
        }
    }

    // Revoke and count statements filter on (UserId, Purpose[, CreatedAt]); if one of them scanned
    // instead of seeking it would lock OTHER users' rows and could deadlock across accounts. Several
    // different users, each hit by verify + resend + re-registration at once, for repeated rounds.
    [SqlServerFact]
    public async Task Verify_Resend_And_Replacement_Across_Many_Different_Users_Never_Deadlock()
    {
        using var db = new SqlServerTestDatabase();
        var clock = new FakeTimeProvider(new DateTimeOffset(DateTime.UtcNow, TimeSpan.Zero));
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        for (var round = 0; round < 10; round++)
        {
            const int users = 6;
            var ids = Enumerable.Range(0, users).Select(_ => Guid.NewGuid()).ToArray();
            for (var u = 0; u < users; u++)
            {
                await db.SeedAsync(
                    TestData.User(ids[u], $"cross{round}-{u}@test.local", passwordHash: $"hashed:{Password}", isEmailConfirmed: false),
                    TokenFor(ids[u], $"cross-{round}-{u}", clock.GetUtcNow().UtcDateTime.AddMinutes(-5)));
            }

            await RaceAsync(users * 3, async i =>
            {
                var u = i / 3;
                var email = $"cross{round}-{u}@test.local";
                await using var context = db.CreateContext();
                var harness = new AuthHarness(context, clock: clock);
                switch (i % 3)
                {
                    case 0:
                        return (await harness.Auth.VerifyEmailAsync(new VerifyEmailRequest { Token = $"cross-{round}-{u}", Password = Password })).IsSuccess;
                    case 1:
                        return (await harness.Auth.ResendVerificationAsync(new ResendVerificationRequest { Email = email })).IsSuccess;
                    default:
                        return (await harness.Auth.RegisterAsync(AuthHarness.Register(email, "AttackerPassword1"))).IsSuccess;
                }
            });
        }

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(90), $"took {stopwatch.Elapsed}");
    }

    [SqlServerFact]
    public async Task Concurrent_Registrations_Of_One_New_Email_Give_One_Created_And_One_Conflict()
    {
        using var db = new SqlServerTestDatabase();
        var clock = new FakeTimeProvider(new DateTimeOffset(DateTime.UtcNow, TimeSpan.Zero));
        var sender = new CaptureEmailSender();

        // Both racers read "no such account" before either writes, so the unique index on
        // Users.Email - not the earlier read - decides.
        var barrier = new AsyncBarrier(2);

        var results = await RaceAsync(2, async i =>
        {
            await using var context = db.CreateContext();
            var harness = new AuthHarness(
                context, clock: clock, sender: sender, decorateStore: inner => new BarrierEmailVerificationStore(inner, barrier));
            return await harness.Auth.RegisterAsync(AuthHarness.Register(Email, $"Password{i}Password"));
        });

        Assert.Equal(1, results.Count(result => result.IsSuccess));
        var loser = Assert.Single(results, result => !result.IsSuccess);
        Assert.Equal("auth.duplicate_email", loser.Error!.Code); // 409, not a 500

        await using var verify = db.CreateContext();
        Assert.Equal(1, await verify.Users.CountAsync(user => user.Email == Email));
        Assert.Equal(1, await verify.UserTokens.CountAsync());
        Assert.Single(sender.Messages); // the loser sent nothing
    }

    [SqlServerFact]
    public async Task Concurrent_Registration_And_External_Sign_In_On_A_New_Email_Do_Not_Crash()
    {
        using var db = new SqlServerTestDatabase();
        var clock = new FakeTimeProvider(new DateTimeOffset(DateTime.UtcNow, TimeSpan.Zero));

        var results = await RaceAsync(2, async i =>
        {
            await using var context = db.CreateContext();
            var harness = new AuthHarness(context, clock: clock);
            if (i == 0)
            {
                var registered = await harness.Auth.RegisterAsync(AuthHarness.Register("both@gmail.com", Password));
                return registered.IsSuccess ? "registered" : registered.Error!.Code;
            }

            // Whichever order the two land in, the loser must end cleanly (a defined outcome, never an exception).
            harness.External.Result = new ExternalUserInfo
            {
                Provider = "google", ProviderUserId = "g-1", Email = "both@gmail.com", FirstName = "G", LastName = "U"
            };
            var external = await harness.Auth.ExternalAsync(new ExternalAuthRequest { Provider = "google", IdToken = "t" });
            return external.IsSuccess ? "external" : external.Error!.Code;
        });

        Assert.DoesNotContain(results, outcome => outcome is null);
        await using var verify = db.CreateContext();
        Assert.Equal(1, await verify.Users.CountAsync(user => user.Email == "both@gmail.com"));
    }
}
