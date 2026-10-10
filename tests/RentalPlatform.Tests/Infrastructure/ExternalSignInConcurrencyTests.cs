using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.DTOs;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Infrastructure;

// The races ADR-030 sections 4 and 7 exist for, against a REAL SQL Server (real concurrent writers,
// real unique-index numbers 2601/2627). Where the interleaving matters, a barrier holds every racer
// after it has read "no linked account yet" so the writes that follow genuinely overlap and the
// outcome is deterministic rather than lucky.
//
// [SqlServerFact]: SKIPPED with a reason when no local SQL Server is reachable.
public sealed class ExternalSignInConcurrencyTests
{
    private const string Password = "Sufficient1Password";
    private static readonly ExternalAuthRequest Request = new() { Provider = "google", IdToken = "t" };

    private static ExternalUserInfo Google(string email, string sub = "g-sub-1") => new()
    {
        Provider = "google", ProviderUserId = sub, Email = email, FirstName = "Gina", LastName = "Google"
    };

    private static async Task<T[]> RaceAsync<T>(int count, Func<int, Task<T>> action)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var racers = Enumerable.Range(0, count).Select(i => Task.Run(async () =>
        {
            await gate.Task;
            return await action(i);
        })).ToArray();

        await Task.Delay(100);
        gate.SetResult();
        return await Task.WhenAll(racers);
    }

    // Holds each caller after its email lookup until all participants have done theirs.
    private sealed class BarrierUserAuthStore : IUserAuthStore
    {
        private readonly IUserAuthStore _inner;
        private readonly AsyncBarrier _barrier;
        private bool _waited;

        public BarrierUserAuthStore(IUserAuthStore inner, AsyncBarrier barrier)
        {
            _inner = inner;
            _barrier = barrier;
        }

        public Task<bool> EmailExistsAsync(string email, CancellationToken cancellationToken = default) => _inner.EmailExistsAsync(email, cancellationToken);

        public async Task<User?> FindByEmailAsync(string email, CancellationToken cancellationToken = default)
        {
            var user = await _inner.FindByEmailAsync(email, cancellationToken);
            if (!_waited)
            {
                _waited = true;
                await _barrier.SignalAndWaitAsync();
            }

            return user;
        }

        public Task<User?> FindByExternalProviderAsync(string provider, string externalProviderId, CancellationToken cancellationToken = default) => _inner.FindByExternalProviderAsync(provider, externalProviderId, cancellationToken);
        public Task<User?> FindByIdAsync(Guid userId, CancellationToken cancellationToken = default) => _inner.FindByIdAsync(userId, cancellationToken);
        public Task AddAsync(User user, CancellationToken cancellationToken = default) => _inner.AddAsync(user, cancellationToken);
        public Task SaveChangesAsync(CancellationToken cancellationToken = default) => _inner.SaveChangesAsync(cancellationToken);
        public Task<bool> TrySaveChangesAsync(CancellationToken cancellationToken = default) => _inner.TrySaveChangesAsync(cancellationToken);
    }

    private static async Task SeedPendingAsync(SqlServerTestDatabase db, Guid id, string email, string? rawToken = null)
    {
        await db.SeedAsync(TestData.User(id, email, passwordHash: $"hashed:{Password}", isEmailConfirmed: false));
        if (rawToken is not null)
        {
            var now = DateTime.UtcNow;
            await db.SeedAsync(new UserToken
            {
                Id = Guid.NewGuid(),
                UserId = id,
                Purpose = TokenPurpose.EmailVerification,
                TokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)),
                CreatedAt = now,
                ExpiresAt = now.AddHours(24)
            });
        }
    }

    // (a) ADR-030 section 4: the conversion may wipe a pending account because it owns nothing.
    [SqlServerFact]
    public async Task A_Pending_Account_Owns_No_Listings_Bookings_Conversations_Or_Favorites_And_Cannot_Sign_In()
    {
        using var db = new SqlServerTestDatabase();
        var clock = new FakeTimeProvider(new DateTimeOffset(DateTime.UtcNow, TimeSpan.Zero));
        await using var context = db.CreateContext();
        var harness = new AuthHarness(context, clock: clock);

        var registered = await harness.Auth.RegisterAsync(AuthHarness.Register("owns.nothing@gmail.com", Password));
        Assert.True(registered.IsSuccess);
        var login = await harness.Auth.LoginAsync(new LoginRequest { Email = "owns.nothing@gmail.com", Password = Password });
        Assert.Equal("auth.email_not_verified", login.Error!.Code); // no JWT is ever issued to it

        await using var verify = db.CreateContext();
        var userId = (await verify.Users.AsNoTracking().SingleAsync(user => user.Email == "owns.nothing@gmail.com")).Id;
        Assert.False(await verify.Listings.AnyAsync(listing => listing.OwnerId == userId));
        Assert.False(await verify.Bookings.AnyAsync(booking => booking.RenterId == userId));
        Assert.False(await verify.Favorites.AnyAsync(favorite => favorite.UserId == userId));
        Assert.False(await verify.Conversations.AnyAsync(conversation => conversation.OwnerId == userId || conversation.RenterId == userId));
    }

    // (b) Conversion vs verify-email: exactly one wins, and the account never ends up verified while
    // holding a password that did not take part in the verification.
    [SqlServerFact]
    public async Task Pending_Conversion_Racing_Verify_Email_Has_Exactly_One_Winner()
    {
        using var db = new SqlServerTestDatabase();
        var clock = new FakeTimeProvider(new DateTimeOffset(DateTime.UtcNow, TimeSpan.Zero));
        var verifyWins = 0;
        var conversionWins = 0;

        for (var round = 0; round < 12; round++)
        {
            var id = Guid.NewGuid();
            var email = $"round{round}@gmail.com";
            await SeedPendingAsync(db, id, email, rawToken: $"token-{round}");

            var outcomes = await RaceAsync(2, async i =>
            {
                await using var context = db.CreateContext();
                var harness = new AuthHarness(context, clock: clock);
                if (i == 0)
                {
                    var verified = await harness.Auth.VerifyEmailAsync(new VerifyEmailRequest { Token = $"token-{round}", Password = Password });
                    return verified.IsSuccess ? "verify:ok" : $"verify:{verified.Error!.Code}";
                }

                harness.External.Result = Google(email, sub: $"g-{round}");
                var external = await harness.Auth.ExternalAsync(Request);
                return external.IsSuccess ? "external:ok" : $"external:{external.Error!.Code}";
            });

            Assert.DoesNotContain(outcomes, outcome => outcome is null);
            await using var verify = db.CreateContext();
            var stored = await verify.Users.AsNoTracking().SingleAsync(user => user.Id == id);
            Assert.True(stored.IsEmailConfirmed);

            if (outcomes[0] == "verify:ok")
            {
                // The registrant proved link + password: their password stays, nothing was wiped.
                verifyWins++;
                Assert.Equal($"hashed:{Password}", stored.PasswordHash);
            }
            else
            {
                // The conversion won: the registrant's password is gone, the link is dead.
                conversionWins++;
                Assert.Equal(string.Empty, stored.PasswordHash);
                Assert.Equal("google", stored.ExternalAuthProvider);
                Assert.Equal("external:ok", outcomes[1]);
            }

            // Either way the verification token was used up or revoked: no live link is left over.
            Assert.All(
                await verify.UserTokens.AsNoTracking().Where(token => token.UserId == id).ToListAsync(),
                token => Assert.NotNull(token.ConsumedAt));
        }

        Assert.Equal(12, verifyWins + conversionWins);
    }

    // (c) Two Google sign-ins on the same pending account: one resets, the other signs in to the same account.
    [SqlServerFact]
    public async Task Two_Concurrent_Google_Sign_Ins_On_One_Pending_Account_Reset_Once_And_Share_The_Account()
    {
        using var db = new SqlServerTestDatabase();
        var clock = new FakeTimeProvider(new DateTimeOffset(DateTime.UtcNow, TimeSpan.Zero));
        var id = Guid.NewGuid();
        await SeedPendingAsync(db, id, "pending.pair@gmail.com", rawToken: "pair-token");
        var barrier = new AsyncBarrier(2);

        var results = await RaceAsync(2, async _ =>
        {
            await using var context = db.CreateContext();
            var harness = new AuthHarness(context, clock: clock, decorateUserStore: inner => new BarrierUserAuthStore(inner, barrier));
            harness.External.Result = Google("pending.pair@gmail.com");
            return await harness.Auth.ExternalAsync(Request);
        });

        Assert.All(results, result => Assert.True(result.IsSuccess, result.Error?.Code));
        Assert.All(results, result => Assert.Equal(id, result.Value!.User.Id));
        await using var verify = db.CreateContext();
        Assert.Equal(1, await verify.Users.CountAsync(user => user.Email == "pending.pair@gmail.com"));
        var stored = await verify.Users.AsNoTracking().SingleAsync(user => user.Id == id);
        Assert.True(stored.IsEmailConfirmed);
        Assert.Equal(string.Empty, stored.PasswordHash);
        Assert.Equal("g-sub-1", stored.ExternalProviderId);
    }

    // (d) Two first sign-ins of one new identity: one user, both 200. Authoritative and not.
    [SqlServerFact]
    public async Task Two_Concurrent_First_Sign_Ins_Of_One_Identity_Create_One_User_And_Both_Succeed()
    {
        using var db = new SqlServerTestDatabase();
        var clock = new FakeTimeProvider(new DateTimeOffset(DateTime.UtcNow, TimeSpan.Zero));

        foreach (var email in new[] { "newcomer@gmail.com", "newcomer@yahoo.com" })
        {
            var barrier = new AsyncBarrier(2);
            var results = await RaceAsync(2, async _ =>
            {
                await using var context = db.CreateContext();
                var harness = new AuthHarness(context, clock: clock, decorateUserStore: inner => new BarrierUserAuthStore(inner, barrier));
                harness.External.Result = Google(email, sub: $"g-{email}");
                return await harness.Auth.ExternalAsync(Request);
            });

            Assert.All(results, result => Assert.True(result.IsSuccess, result.Error?.Code));
            Assert.Equal(results[0].Value!.User.Id, results[1].Value!.User.Id);
            await using var verify = db.CreateContext();
            Assert.Equal(1, await verify.Users.CountAsync(user => user.Email == email));
        }
    }

    // (e) One identity linking to two different confirmed accounts at once: one success, one 409.
    [SqlServerFact]
    public async Task One_Identity_Linking_Concurrently_To_Two_Confirmed_Accounts_Succeeds_Once_And_Conflicts_Once()
    {
        using var db = new SqlServerTestDatabase();
        var clock = new FakeTimeProvider(new DateTimeOffset(DateTime.UtcNow, TimeSpan.Zero));
        await db.SeedAsync(
            TestData.User(Guid.NewGuid(), "first.target@gmail.com", passwordHash: $"hashed:{Password}"),
            TestData.User(Guid.NewGuid(), "second.target@gmail.com", passwordHash: $"hashed:{Password}"));
        var barrier = new AsyncBarrier(2);

        var results = await RaceAsync(2, async i =>
        {
            await using var context = db.CreateContext();
            var harness = new AuthHarness(context, clock: clock, decorateUserStore: inner => new BarrierUserAuthStore(inner, barrier));
            harness.External.Result = Google(i == 0 ? "first.target@gmail.com" : "second.target@gmail.com", sub: "shared-identity");
            return await harness.Auth.ExternalAsync(Request);
        });

        Assert.Equal(1, results.Count(result => result.IsSuccess));
        var loser = Assert.Single(results, result => !result.IsSuccess);
        Assert.Equal("auth.external_link_conflict", loser.Error!.Code); // 409, not a 500
        await using var verify = db.CreateContext();
        Assert.Equal(1, await verify.Users.CountAsync(user => user.ExternalProviderId == "shared-identity"));
    }
}
