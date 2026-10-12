using Microsoft.EntityFrameworkCore;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.DTOs;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Auth;

// ADR-030 sections 4-6 and ADR-028 section 10 at the service level, over a real (SQLite) database:
// the linking / pending / blocked decision table, names, language and the link race.
public sealed class ExternalSignInResolutionTests
{
    private static readonly Guid AccountId = new("e3000000-0000-0000-0000-000000000001");
    private const string RealHash = "hashed:RealPassword1";
    private static readonly ExternalAuthRequest Request = new() { Provider = "google", IdToken = "t" };

    private static ExternalUserInfo Google(
        string email = "someone@gmail.com",
        string? hostedDomain = null,
        string sub = "google-sub-1",
        string? first = "Gina",
        string? last = "Google",
        string? full = "Gina Google") => new()
    {
        Provider = "google",
        ProviderUserId = sub,
        Email = email,
        FirstName = first,
        LastName = last,
        FullName = full,
        HostedDomain = hostedDomain
    };

    private static User Confirmed(string email, bool blocked = false) =>
        TestData.User(AccountId, email, isBlocked: blocked, passwordHash: RealHash, firstName: "Original", lastName: "Name");

    private static User Pending(string email, bool blocked = false) =>
        TestData.User(AccountId, email, isBlocked: blocked, passwordHash: RealHash, isEmailConfirmed: false, firstName: "Squatter", lastName: "Name");

    private static async Task<User> UserAsync(SqliteTestDatabase db, string email)
    {
        await using var context = db.CreateContext();
        return await context.Users.AsNoTracking().SingleAsync(user => user.Email == email);
    }

    private static async Task<int> UserCountAsync(SqliteTestDatabase db)
    {
        await using var context = db.CreateContext();
        return await context.Users.CountAsync();
    }

    private static UserToken ActiveToken(Guid userId) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        Purpose = RentalPlatform.Domain.Enums.TokenPurpose.EmailVerification,
        TokenHash = new byte[32],
        CreatedAt = DateTime.UtcNow,
        ExpiresAt = DateTime.UtcNow.AddHours(24)
    };

    private static async Task<(SqliteTestDatabase Db, AuthHarness Harness, AppDbContextHolder Holder)> ArrangeAsync(
        ExternalUserInfo info, params object[] seed)
    {
        var db = new SqliteTestDatabase();
        if (seed.Length > 0)
        {
            await db.SeedAsync(seed);
        }

        var holder = new AppDbContextHolder(db.CreateContext());
        var harness = new AuthHarness(holder.Context);
        harness.External.Result = info;
        return (db, harness, holder);
    }

    private sealed class AppDbContextHolder : IAsyncDisposable
    {
        public AppDbContextHolder(RentalPlatform.Infrastructure.Persistence.AppDbContext context) => Context = context;
        public RentalPlatform.Infrastructure.Persistence.AppDbContext Context { get; }
        public ValueTask DisposeAsync() => Context.DisposeAsync();
    }

    // ---- The decision table ----------------------------------------------------------------------------

    [Fact]
    public async Task Case1_Confirmed_Gmail_Account_Is_Linked_And_Signed_In_Without_Touching_Password_Or_Name()
    {
        var (db, h, holder) = await ArrangeAsync(Google("someone@gmail.com"), Confirmed("someone@gmail.com"));
        using var _ = db;
        await using var __ = holder;

        var result = await h.Auth.ExternalAsync(Request);

        Assert.True(result.IsSuccess);
        var stored = await UserAsync(db, "someone@gmail.com");
        Assert.Equal("google", stored.ExternalAuthProvider);
        Assert.Equal("google-sub-1", stored.ExternalProviderId);
        Assert.Equal(RealHash, stored.PasswordHash);
        Assert.Equal("Original", stored.FirstName);
        Assert.Equal("Name", stored.LastName);
    }

    [Fact]
    public async Task Case2_Confirmed_Workspace_Account_Is_Linked_When_hd_Equals_The_Email_Domain()
    {
        var (db, h, holder) = await ArrangeAsync(Google("boss@Corp.Example", hostedDomain: "corp.example"), Confirmed("boss@corp.example"));
        using var _ = db;
        await using var __ = holder;

        var result = await h.Auth.ExternalAsync(Request);

        Assert.True(result.IsSuccess);
        Assert.Equal("google", (await UserAsync(db, "boss@corp.example")).ExternalAuthProvider);
    }

    [Theory]
    [InlineData("someone@yahoo.com", null)]
    [InlineData("someone@yahoo.com", "other.org")]
    [InlineData("someone@mail.corp.example", "corp.example")] // subdomain is not the hd domain
    [InlineData("someone@gmail.com.evil.test", null)]
    public async Task Case3_Confirmed_Account_With_A_Non_Authoritative_Email_Is_A_Link_Conflict_And_Nothing_Is_Written(
        string email, string? hostedDomain)
    {
        var (db, h, holder) = await ArrangeAsync(Google(email, hostedDomain), Confirmed(email));
        using var _ = db;
        await using var __ = holder;

        var result = await h.Auth.ExternalAsync(Request);

        Assert.False(result.IsSuccess);
        Assert.Equal("auth.external_link_conflict", result.Error!.Code);
        var stored = await UserAsync(db, email);
        Assert.Null(stored.ExternalAuthProvider);
        Assert.Null(stored.ExternalProviderId);
        Assert.Equal(RealHash, stored.PasswordHash);
    }

    [Fact]
    public async Task Case3b_An_Unverified_Email_With_An_Unknown_Identity_Is_email_missing()
    {
        var info = Google();
        var withoutEmail = new ExternalUserInfo
        {
            Provider = info.Provider, ProviderUserId = info.ProviderUserId, Email = null, FirstName = "G"
        };
        var (db, h, holder) = await ArrangeAsync(withoutEmail, Confirmed("someone@gmail.com"));
        using var _ = db;
        await using var __ = holder;

        var result = await h.Auth.ExternalAsync(Request);

        Assert.Equal("auth.external_email_missing", result.Error!.Code);
        Assert.Null((await UserAsync(db, "someone@gmail.com")).ExternalAuthProvider);
    }

    [Fact]
    public async Task Case4_Pending_Registration_With_An_Authoritative_Email_Is_Reset_And_Signed_In()
    {
        var (db, h, holder) = await ArrangeAsync(Google("someone@gmail.com"), Pending("someone@gmail.com"), ActiveToken(AccountId));
        using var _ = db;
        await using var __ = holder;

        var result = await h.Auth.ExternalAsync(new ExternalAuthRequest { Provider = "google", IdToken = "t", PreferredLanguage = "hy" });

        Assert.True(result.IsSuccess);
        var stored = await UserAsync(db, "someone@gmail.com");
        Assert.True(stored.IsEmailConfirmed);
        Assert.Equal(string.Empty, stored.PasswordHash);
        Assert.Equal("Gina", stored.FirstName);
        Assert.Equal("Google", stored.LastName);
        Assert.Equal("hy", stored.PreferredLanguage);
        Assert.Equal("google", stored.ExternalAuthProvider);
        await using var context = db.CreateContext();
        Assert.All(await context.UserTokens.AsNoTracking().ToListAsync(), token => Assert.NotNull(token.ConsumedAt));
    }

    [Fact]
    public async Task Case4_Pending_Reset_With_An_Unsupported_Language_Stores_Null()
    {
        var (db, h, holder) = await ArrangeAsync(Google("someone@gmail.com"), Pending("someone@gmail.com"));
        using var _ = db;
        await using var __ = holder;

        await h.Auth.ExternalAsync(new ExternalAuthRequest { Provider = "google", IdToken = "t", PreferredLanguage = "de" });

        Assert.Null((await UserAsync(db, "someone@gmail.com")).PreferredLanguage);
    }

    [Fact]
    public async Task Case5_Pending_Registration_With_A_Non_Authoritative_Email_Is_Not_Reset()
    {
        var (db, h, holder) = await ArrangeAsync(Google("someone@yahoo.com"), Pending("someone@yahoo.com"), ActiveToken(AccountId));
        using var _ = db;
        await using var __ = holder;

        var result = await h.Auth.ExternalAsync(Request);

        Assert.False(result.IsSuccess);
        Assert.Equal("auth.external_pending_registration", result.Error!.Code);
        var stored = await UserAsync(db, "someone@yahoo.com");
        Assert.False(stored.IsEmailConfirmed);
        Assert.Equal(RealHash, stored.PasswordHash);
        Assert.Equal("Squatter", stored.FirstName);
        Assert.Null(stored.ExternalAuthProvider);
        await using var context = db.CreateContext();
        Assert.All(await context.UserTokens.AsNoTracking().ToListAsync(), token => Assert.Null(token.ConsumedAt));
    }

    [Fact]
    public async Task Case6_Blocked_By_Identity_Is_Refused()
    {
        var blocked = Confirmed("someone@gmail.com", blocked: true);
        blocked.ExternalAuthProvider = "google";
        blocked.ExternalProviderId = "google-sub-1";
        var (db, h, holder) = await ArrangeAsync(Google("someone@gmail.com"), blocked);
        using var _ = db;
        await using var __ = holder;

        var result = await h.Auth.ExternalAsync(Request);

        Assert.Equal("auth.user_blocked", result.Error!.Code);
    }

    [Fact]
    public async Task Case6_Blocked_Confirmed_Account_Found_By_Email_Is_Refused_Before_Any_Link_Is_Written()
    {
        var (db, h, holder) = await ArrangeAsync(Google("someone@gmail.com"), Confirmed("someone@gmail.com", blocked: true));
        using var _ = db;
        await using var __ = holder;

        var result = await h.Auth.ExternalAsync(Request);

        Assert.Equal("auth.user_blocked", result.Error!.Code);
        var stored = await UserAsync(db, "someone@gmail.com");
        Assert.Null(stored.ExternalAuthProvider);
        Assert.Null(stored.ExternalProviderId);
        Assert.Equal("Original", stored.FirstName);
    }

    [Fact]
    public async Task Case6_Blocked_Pending_Account_Is_Refused_And_Not_Reset()
    {
        var (db, h, holder) = await ArrangeAsync(Google("someone@gmail.com"), Pending("someone@gmail.com", blocked: true), ActiveToken(AccountId));
        using var _ = db;
        await using var __ = holder;

        var result = await h.Auth.ExternalAsync(Request);

        Assert.Equal("auth.user_blocked", result.Error!.Code);
        var stored = await UserAsync(db, "someone@gmail.com");
        Assert.False(stored.IsEmailConfirmed);
        Assert.Equal(RealHash, stored.PasswordHash);
        Assert.Null(stored.ExternalProviderId);
        await using var context = db.CreateContext();
        Assert.All(await context.UserTokens.AsNoTracking().ToListAsync(), token => Assert.Null(token.ConsumedAt));
    }

    [Theory]
    [InlineData("apple", "apple-sub-9", "google-sub-1")] // linked to another provider
    [InlineData("google", "google-sub-OTHER", "google-sub-1")] // linked to another Google identity
    public async Task Case7_Confirmed_Account_Already_Linked_Elsewhere_Is_A_Link_Conflict(
        string existingProvider, string existingSub, string incomingSub)
    {
        var linked = Confirmed("someone@gmail.com");
        linked.ExternalAuthProvider = existingProvider;
        linked.ExternalProviderId = existingSub;
        var (db, h, holder) = await ArrangeAsync(Google("someone@gmail.com", sub: incomingSub), linked);
        using var _ = db;
        await using var __ = holder;

        var result = await h.Auth.ExternalAsync(Request);

        Assert.Equal("auth.external_link_conflict", result.Error!.Code);
        var stored = await UserAsync(db, "someone@gmail.com");
        Assert.Equal(existingProvider, stored.ExternalAuthProvider);
        Assert.Equal(existingSub, stored.ExternalProviderId);
    }

    [Fact]
    public async Task A_Non_Authoritative_Email_With_No_Account_Still_Creates_A_User()
    {
        var (db, h, holder) = await ArrangeAsync(Google("someone@yahoo.com"));
        using var _ = db;
        await using var __ = holder;

        var result = await h.Auth.ExternalAsync(Request);

        Assert.True(result.IsSuccess);
        var stored = await UserAsync(db, "someone@yahoo.com");
        Assert.True(stored.IsEmailConfirmed);
        Assert.Equal("google", stored.ExternalAuthProvider);
    }

    [Fact]
    public async Task Sign_In_By_Identity_Needs_No_Email_Rules_And_Writes_Nothing()
    {
        var linked = Confirmed("someone@yahoo.com");
        linked.ExternalAuthProvider = "google";
        linked.ExternalProviderId = "google-sub-1";
        var (db, h, holder) = await ArrangeAsync(Google("someone@yahoo.com"), linked);
        using var _ = db;
        await using var __ = holder;

        Assert.True((await h.Auth.ExternalAsync(Request)).IsSuccess);
        Assert.Equal("Original", (await UserAsync(db, "someone@yahoo.com")).FirstName);
    }

    // ---- Names (ADR-030 section 5) -----------------------------------------------------------------------

    [Theory]
    [InlineData("Gina", "Google", "Gina Google", "Gina", "Google")]
    [InlineData(null, null, "Full Name Only", "Full Name Only", "")]
    [InlineData("  ", " ", "Full Name Only", "Full Name Only", "")]
    [InlineData(null, "Family", null, "", "Family")]
    [InlineData(null, null, null, "", "")]
    [InlineData("  Padded  ", "  Names ", null, "Padded", "Names")]
    public async Task New_User_Names_Come_From_Google_And_Are_Never_The_Email(
        string? given, string? family, string? full, string expectedFirst, string expectedLast)
    {
        var (db, h, holder) = await ArrangeAsync(Google("secret.local.part@gmail.com", first: given, last: family, full: full));
        using var _ = db;
        await using var __ = holder;

        Assert.True((await h.Auth.ExternalAsync(Request)).IsSuccess);

        var stored = await UserAsync(db, "secret.local.part@gmail.com");
        Assert.Equal(expectedFirst, stored.FirstName);
        Assert.Equal(expectedLast, stored.LastName);
        Assert.DoesNotContain("secret", stored.FirstName + stored.LastName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Names_Longer_Than_100_Characters_Are_Truncated_To_The_Column_Size()
    {
        var (db, h, holder) = await ArrangeAsync(Google("long@gmail.com", first: new string('a', 150), last: new string('b', 101)));
        using var _ = db;
        await using var __ = holder;

        Assert.True((await h.Auth.ExternalAsync(Request)).IsSuccess);

        var stored = await UserAsync(db, "long@gmail.com");
        Assert.Equal(100, stored.FirstName.Length);
        Assert.Equal(100, stored.LastName.Length);
    }

    [Fact]
    public async Task Truncation_Does_Not_Split_A_Surrogate_Pair()
    {
        var first = new string('a', 99) + "\U0001F600"; // 99 + a 2-char emoji = 101 chars
        var (db, h, holder) = await ArrangeAsync(Google("emoji@gmail.com", first: first));
        using var _ = db;
        await using var __ = holder;

        Assert.True((await h.Auth.ExternalAsync(Request)).IsSuccess);

        var stored = await UserAsync(db, "emoji@gmail.com");
        Assert.Equal(new string('a', 99), stored.FirstName);
    }

    [Fact]
    public async Task Pending_Reset_Takes_Names_From_Google_Not_From_The_Email()
    {
        var (db, h, holder) = await ArrangeAsync(Google("hidden.part@gmail.com", first: null, last: null, full: null), Pending("hidden.part@gmail.com"));
        using var _ = db;
        await using var __ = holder;

        Assert.True((await h.Auth.ExternalAsync(Request)).IsSuccess);

        var stored = await UserAsync(db, "hidden.part@gmail.com");
        Assert.Equal(string.Empty, stored.FirstName);
        Assert.Equal(string.Empty, stored.LastName);
    }

    // ---- Language (ADR-030 section 6, M-056) ------------------------------------------------------------

    [Theory]
    [InlineData("hy", "hy")]
    [InlineData("RU", "ru")]
    [InlineData(" en ", "en")]
    [InlineData("de", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("hy-AM", null)]
    public async Task New_User_Gets_The_Requested_Language_When_It_Is_Allowed(string? requested, string? expected)
    {
        var (db, h, holder) = await ArrangeAsync(Google("lang@gmail.com"));
        using var _ = db;
        await using var __ = holder;

        Assert.True((await h.Auth.ExternalAsync(new ExternalAuthRequest { Provider = "google", IdToken = "t", PreferredLanguage = requested })).IsSuccess);

        Assert.Equal(expected, (await UserAsync(db, "lang@gmail.com")).PreferredLanguage);
    }

    [Fact]
    public async Task Linking_Never_Overwrites_The_Existing_Language()
    {
        var existing = Confirmed("lang@gmail.com");
        existing.PreferredLanguage = "ru";
        var (db, h, holder) = await ArrangeAsync(Google("lang@gmail.com"), existing);
        using var _ = db;
        await using var __ = holder;

        await h.Auth.ExternalAsync(new ExternalAuthRequest { Provider = "google", IdToken = "t", PreferredLanguage = "hy" });

        Assert.Equal("ru", (await UserAsync(db, "lang@gmail.com")).PreferredLanguage);
    }

    // ---- Races -----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_Unique_Violation_On_The_Link_Write_Is_A_409_Not_A_500()
    {
        // Two confirmed gmail accounts; google-sub-1 is concurrently linked to the OTHER one between
        // our read and our write, so our save hits the unique index on (provider, id).
        var secondId = Guid.NewGuid();
        var first = Confirmed("first@gmail.com");
        var second = TestData.User(secondId, "second@gmail.com", passwordHash: RealHash);
        var db = new SqliteTestDatabase();
        using var _ = db;
        await db.SeedAsync(first, second);
        await using var context = db.CreateContext();
        var h = new AuthHarness(
            context,
            decorateUserStore: inner => new LinkRivalStore(inner, async () =>
            {
                await using var rival = db.CreateContext();
                await rival.Users.Where(user => user.Id == secondId).ExecuteUpdateAsync(set => set
                    .SetProperty(user => user.ExternalAuthProvider, "google")
                    .SetProperty(user => user.ExternalProviderId, "google-sub-1"));
            }));
        h.External.Result = Google("first@gmail.com");

        var result = await h.Auth.ExternalAsync(Request);

        Assert.False(result.IsSuccess);
        Assert.Equal("auth.external_link_conflict", result.Error!.Code);
        Assert.Null((await UserAsync(db, "first@gmail.com")).ExternalAuthProvider);
        Assert.Equal("google-sub-1", (await UserAsync(db, "second@gmail.com")).ExternalProviderId);
    }

    // Runs the rival write right before the first TrySaveChanges, i.e. after the service decided to link.
    private sealed class LinkRivalStore : IUserAuthStore
    {
        private readonly IUserAuthStore _inner;
        private readonly Func<Task> _rival;
        private bool _ran;

        public LinkRivalStore(IUserAuthStore inner, Func<Task> rival)
        {
            _inner = inner;
            _rival = rival;
        }

        public Task<bool> EmailExistsAsync(string email, CancellationToken cancellationToken = default) => _inner.EmailExistsAsync(email, cancellationToken);
        public Task<User?> FindByEmailAsync(string email, CancellationToken cancellationToken = default) => _inner.FindByEmailAsync(email, cancellationToken);
        public Task<User?> FindByExternalProviderAsync(string provider, string externalProviderId, CancellationToken cancellationToken = default) => _inner.FindByExternalProviderAsync(provider, externalProviderId, cancellationToken);
        public Task<User?> FindByIdAsync(Guid userId, CancellationToken cancellationToken = default) => _inner.FindByIdAsync(userId, cancellationToken);
        public Task AddAsync(User user, CancellationToken cancellationToken = default) => _inner.AddAsync(user, cancellationToken);
        public Task SaveChangesAsync(CancellationToken cancellationToken = default) => _inner.SaveChangesAsync(cancellationToken);

        public async Task<bool> TrySaveChangesAsync(CancellationToken cancellationToken = default)
        {
            if (!_ran)
            {
                _ran = true;
                await _rival();
            }

            return await _inner.TrySaveChangesAsync(cancellationToken);
        }
    }

    [Fact]
    public async Task TrySaveChanges_Maps_Only_A_Unique_Violation_Other_Database_Errors_Propagate()
    {
        using var db = new SqliteTestDatabase();
        var other = TestData.User(Guid.NewGuid(), "other@gmail.com");
        other.ExternalAuthProvider = "google";
        other.ExternalProviderId = "taken";
        await db.SeedAsync(other, Confirmed("mine@gmail.com"));

        // Unique violation -> false.
        await using (var context = db.CreateContext())
        {
            var store = new RentalPlatform.Infrastructure.Persistence.UserAuthStore(context);
            var mine = (await store.FindByEmailAsync("mine@gmail.com"))!;
            mine.ExternalAuthProvider = "google";
            mine.ExternalProviderId = "taken";
            Assert.False(await store.TrySaveChangesAsync());
        }

        // A foreign-key violation (a district that does not exist) is NOT swallowed.
        await using (var context = db.CreateContext())
        {
            var store = new RentalPlatform.Infrastructure.Persistence.UserAuthStore(context);
            var mine = (await store.FindByEmailAsync("mine@gmail.com"))!;
            mine.HomeDistrictId = Guid.NewGuid();
            await Assert.ThrowsAsync<DbUpdateException>(() => store.TrySaveChangesAsync());
        }

        // The happy path saves.
        await using (var context = db.CreateContext())
        {
            var store = new RentalPlatform.Infrastructure.Persistence.UserAuthStore(context);
            var mine = (await store.FindByEmailAsync("mine@gmail.com"))!;
            mine.FirstName = "Changed";
            Assert.True(await store.TrySaveChangesAsync());
        }

        Assert.Equal("Changed", (await UserAsync(db, "mine@gmail.com")).FirstName);
    }

    [Fact]
    public async Task Losing_The_First_Sign_In_Race_For_The_Same_Identity_Signs_Into_The_Winners_Account()
    {
        // A non-authoritative email on purpose: if the retry only looked at the email it would see a
        // confirmed account it may not link to and answer 409. The identity lookup must come first.
        using var db = new SqliteTestDatabase();
        await using var context = db.CreateContext();
        var winnerId = Guid.NewGuid();
        var h = new AuthHarness(
            context,
            decorateUserStore: inner => new RacingUserAuthStore(inner, () =>
            {
                var winner = TestData.User(winnerId, "fresh@yahoo.com", passwordHash: string.Empty);
                winner.ExternalAuthProvider = "google";
                winner.ExternalProviderId = "google-sub-1";
                return db.SeedAsync(winner);
            }));
        h.External.Result = Google("fresh@yahoo.com");

        var result = await h.Auth.ExternalAsync(Request);

        Assert.True(result.IsSuccess);
        Assert.Equal(winnerId, result.Value!.User.Id);
        Assert.Equal(1, await UserCountAsync(db));
    }
}
