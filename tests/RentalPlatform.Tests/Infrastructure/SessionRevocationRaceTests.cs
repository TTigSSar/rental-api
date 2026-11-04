using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RentalPlatform.Api.Hubs;
using RentalPlatform.Application.DTOs;
using RentalPlatform.Infrastructure.Services;
using RentalPlatform.Tests.TestSupport;
using Xunit;
using static RentalPlatform.Tests.TestSupport.SessionRevocationTestSupport;

namespace RentalPlatform.Tests.Infrastructure;

// ADR-055: the races the design exists for. Where the interleaving matters it is forced with a hook that
// runs the rival write at the exact point between the victim's read and its write, so each outcome is
// deterministic rather than lucky (SQLite). Real BCrypt hashing, the real token service, the real stores.
// The one genuinely concurrent test (two simultaneous password changes) needs a real SQL Server.
[Collection(RentalPlatform.Tests.TestSupport.SqlServerConcurrencyCollection.Name)]
public sealed class SessionRevocationRaceTests
{
    private const string Email = "race.session@test.local";
    private const string OldPassword = "OldPassword123";
    private const string NewPassword = "BrandNewPassword9";
    private static readonly Guid UserId = new("a7000000-0000-0000-0000-000000000001");

    private static readonly BcryptPasswordHasher Hasher = new();

    private static ChatConnectionRegistry NewRegistry() => new(NullLogger<ChatConnectionRegistry>.Instance);

    private static async Task SeedAsync(SqliteTestDatabase db) =>
        await db.SeedAsync(TestData.User(UserId, Email, passwordHash: Hasher.HashPassword(OldPassword)));

    private static async Task<int> StoredVersionAsync(SqliteTestDatabase db)
    {
        await using var context = db.CreateContext();
        return (await context.Users.AsNoTracking().SingleAsync(user => user.Id == UserId)).TokenVersion;
    }

    // (a) The login read the user before a password change committed. The password it verified is the old
    // one, the token it issues carries the old version, and the pipeline's check rejects it.
    [Fact]
    public async Task A_Login_Racing_A_Password_Change_Issues_A_Token_The_Check_Rejects()
    {
        using var db = new SqliteTestDatabase();
        await SeedAsync(db);
        var cache = NewCache(new DbReader(db.CreateContext));
        var revoker = new SessionRevoker(cache, NewRegistry());
        var jwt = RealTokenService();
        await cache.GetAsync(UserId); // the old state is cached, as for a user with a live session

        await using var loginContext = db.CreateContext();
        var login = new AuthHarness(loginContext, Hasher, jwtTokenService: jwt, decorateUserStore: inner =>
            new HookUserAuthStore(inner, afterFindByEmail: async () =>
            {
                await using var context = db.CreateContext();
                var change = new AuthHarness(context, Hasher, sessionRevoker: revoker, jwtTokenService: jwt, currentUserId: UserId);
                Assert.True((await change.Auth.ChangePasswordAsync(OldPassword, NewPassword)).IsSuccess);
            }));

        var result = await login.Auth.LoginAsync(new LoginRequest { Email = Email, Password = OldPassword });

        Assert.True(result.IsSuccess);
        Assert.Equal(0, TokenVersionClaim(result.Value!.AccessToken));
        Assert.Equal(1, await StoredVersionAsync(db));
        Assert.False(await SessionTokenValidator.IsAcceptedAsync(PrincipalOf(result.Value.AccessToken), cache, default));
    }

    // (b) The same race against a password reset.
    [Fact]
    public async Task A_Login_Racing_A_Password_Reset_Issues_A_Token_The_Check_Rejects()
    {
        using var db = new SqliteTestDatabase();
        await SeedAsync(db);
        var clock = new FakeTimeProvider(new DateTimeOffset(DateTime.UtcNow, TimeSpan.Zero));
        var cache = NewCache(new DbReader(db.CreateContext));
        var revoker = new SessionRevoker(cache, NewRegistry());
        var jwt = RealTokenService();
        await cache.GetAsync(UserId);

        string token;
        await using (var forgotContext = db.CreateContext())
        {
            var forgot = new AuthHarness(forgotContext, Hasher, clock: clock);
            await forgot.Auth.ForgotPasswordAsync(new ForgotPasswordRequest { Email = Email });
            token = forgot.Sender.LastTokenFor(Email)!;
        }

        await using var loginContext = db.CreateContext();
        var login = new AuthHarness(loginContext, Hasher, clock: clock, jwtTokenService: jwt, decorateUserStore: inner =>
            new HookUserAuthStore(inner, afterFindByEmail: async () =>
            {
                await using var context = db.CreateContext();
                var reset = new AuthHarness(context, Hasher, clock: clock, sessionRevoker: revoker, jwtTokenService: jwt);
                Assert.True((await reset.Auth.ResetPasswordAsync(new ResetPasswordRequest { Token = token, NewPassword = NewPassword })).IsSuccess);
            }));

        var result = await login.Auth.LoginAsync(new LoginRequest { Email = Email, Password = OldPassword });

        Assert.True(result.IsSuccess);
        Assert.Equal(0, TokenVersionClaim(result.Value!.AccessToken));
        Assert.Equal(1, await StoredVersionAsync(db));
        Assert.False(await SessionTokenValidator.IsAcceptedAsync(PrincipalOf(result.Value.AccessToken), cache, default));
    }

    // The reset's own response carries a token of the NEW version (ADR-035 section 1 re-read), which is accepted.
    [Fact]
    public async Task The_Token_Returned_By_A_Reset_Carries_The_New_Version_And_Is_Accepted()
    {
        using var db = new SqliteTestDatabase();
        await SeedAsync(db);
        var clock = new FakeTimeProvider(new DateTimeOffset(DateTime.UtcNow, TimeSpan.Zero));
        var cache = NewCache(new DbReader(db.CreateContext));
        var revoker = new FakeSessionRevoker();

        await using var context = db.CreateContext();
        var h = new AuthHarness(context, Hasher, clock: clock, sessionRevoker: revoker, jwtTokenService: RealTokenService());
        await h.Auth.ForgotPasswordAsync(new ForgotPasswordRequest { Email = Email });

        var result = await h.Auth.ResetPasswordAsync(
            new ResetPasswordRequest { Token = h.Sender.LastTokenFor(Email)!, NewPassword = NewPassword });

        Assert.True(result.IsSuccess);
        Assert.Equal(1, TokenVersionClaim(result.Value!.AccessToken));
        Assert.Equal(new[] { UserId }, revoker.Revoked);
        Assert.True(await SessionTokenValidator.IsAcceptedAsync(PrincipalOf(result.Value.AccessToken), cache, default));
    }

    // (c) A cache fill whose database read finished BEFORE a rotation committed must not store its value
    // AFTER the invalidation. Without the epoch the stale (0) entry would be served for 60 s.
    [Fact]
    public async Task A_Cache_Fill_Racing_An_Invalidation_Does_Not_Store_The_Stale_Value()
    {
        using var db = new SqliteTestDatabase();
        await SeedAsync(db);
        var gated = new GatedReader(new DbReader(db.CreateContext));
        var cache = NewCache(gated);
        var oldToken = TokenFor(UserId, 0);

        var filling = cache.GetAsync(UserId);   // reads version 0, then pauses before storing it
        await gated.ReadCompleted;

        await using (var context = db.CreateContext())
        {
            await context.Users.Where(user => user.Id == UserId)
                .ExecuteUpdateAsync(set => set.SetProperty(user => user.TokenVersion, user => user.TokenVersion + 1));
        }

        cache.Invalidate(UserId);               // the rotation's post-commit invalidation
        gated.Release();
        var stale = await filling;              // the in-flight caller still gets what it read...

        Assert.Equal(0, stale!.Value.TokenVersion);
        // ...but it was not cached: the next validation reads the database and rejects the old token.
        Assert.False(await SessionTokenValidator.IsAcceptedAsync(PrincipalOf(oldToken), cache, default));
        Assert.True(await SessionTokenValidator.IsAcceptedAsync(PrincipalOf(TokenFor(UserId, 1)), cache, default));
    }

    [Fact]
    public async Task A_Missing_User_Is_Not_Cached_So_A_Later_Created_User_Is_Seen()
    {
        using var db = new SqliteTestDatabase();
        var cache = NewCache(new DbReader(db.CreateContext));

        Assert.Null(await cache.GetAsync(UserId));
        await SeedAsync(db);

        Assert.NotNull(await cache.GetAsync(UserId));
    }

    // (d, deterministic) Both changes read the same row; the rival commits first. The loser's conditional
    // UPDATE matches nothing and is told its current password is wrong, never silently overwriting.
    [Fact]
    public async Task A_Password_Change_Whose_Row_Changed_Since_It_Was_Read_Loses_With_400_And_Overwrites_Nothing()
    {
        using var db = new SqliteTestDatabase();
        await SeedAsync(db);

        await using var loserContext = db.CreateContext();
        var loser = new AuthHarness(loserContext, Hasher, currentUserId: UserId, decorateUserStore: inner =>
            new HookUserAuthStore(inner, afterFindById: async () =>
            {
                await using var context = db.CreateContext();
                var winner = new AuthHarness(context, Hasher, currentUserId: UserId);
                Assert.True((await winner.Auth.ChangePasswordAsync(OldPassword, "WinnerPassword1")).IsSuccess);
            }));

        var result = await loser.Auth.ChangePasswordAsync(OldPassword, "LoserPassword1");

        Assert.False(result.IsSuccess);
        Assert.Equal("auth.invalid_current_password", result.Error!.Code);
        await using var verify = db.CreateContext();
        var stored = await verify.Users.AsNoTracking().SingleAsync(user => user.Id == UserId);
        Assert.True(Hasher.VerifyPassword("WinnerPassword1", stored.PasswordHash));
        Assert.Equal(1, stored.TokenVersion); // rotated once, not twice
    }

    // The block lands between the change's read and its write: the account stays blocked and unchanged.
    [Fact]
    public async Task A_Password_Change_Racing_A_Block_Answers_Blocked_And_Changes_Nothing()
    {
        using var db = new SqliteTestDatabase();
        await SeedAsync(db);
        var revoker = new FakeSessionRevoker();

        await using var context = db.CreateContext();
        var change = new AuthHarness(context, Hasher, currentUserId: UserId, sessionRevoker: revoker, decorateUserStore: inner =>
            new HookUserAuthStore(inner, afterFindById: async () =>
            {
                await using var other = db.CreateContext();
                await other.Users.Where(user => user.Id == UserId)
                    .ExecuteUpdateAsync(set => set.SetProperty(user => user.IsBlocked, true));
            }));

        var result = await change.Auth.ChangePasswordAsync(OldPassword, NewPassword);

        Assert.False(result.IsSuccess);
        Assert.Equal("auth.user_blocked", result.Error!.Code);
        Assert.Empty(revoker.Revoked);
        await using var verify = db.CreateContext();
        var stored = await verify.Users.AsNoTracking().SingleAsync(user => user.Id == UserId);
        Assert.True(Hasher.VerifyPassword(OldPassword, stored.PasswordHash));
    }

    // (d, real) Eight simultaneous changes from the same state: exactly one wins, and the version moved once.
    [SqlServerFact]
    public async Task Concurrent_Password_Changes_Have_Exactly_One_Winner()
    {
        using var db = new SqlServerTestDatabase();
        await db.SeedAsync(TestData.User(UserId, Email, passwordHash: $"hashed:{OldPassword}"));

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var racers = Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
        {
            await gate.Task;
            await using var context = db.CreateContext();
            var h = new AuthHarness(context, currentUserId: UserId);
            return await h.Auth.ChangePasswordAsync(OldPassword, $"RacingPassword-{i}");
        })).ToArray();
        await Task.Delay(100);
        gate.SetResult();
        var results = await Task.WhenAll(racers);

        Assert.Equal(1, results.Count(result => result.IsSuccess));
        Assert.All(results.Where(result => !result.IsSuccess), result => Assert.Equal("auth.invalid_current_password", result.Error!.Code));
        await using var verify = db.CreateContext();
        var stored = await verify.Users.AsNoTracking().SingleAsync(user => user.Id == UserId);
        Assert.Equal(1, stored.TokenVersion);
    }
}
