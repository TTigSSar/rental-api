using System.Reflection;
using Microsoft.EntityFrameworkCore;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.DTOs;
using RentalPlatform.Application.Services;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Auth;

// Account takeover via a collation-equal address: SQL Server's default CI_AS collation treats
// N'ß' = N'ss' (and fullwidth / ignorable code points) as equal, so the lookup returns the victim's row
// for the attacker's lookalike address. The link must go to the STORED address, and a row matched by a
// different string is treated as unknown. SQLite does not reproduce the collation, so the service tests
// fold the lookup in a store decorator; the [SqlServerFact] proves the real collation match.
public sealed class CollationEqualEmailTests
{
    private const string Stored = "victim@business.example";
    private const string Twin = "victim@busineß.example";
    private const string Fullwidth = "victim@ｂusiness.example";
    private static readonly Guid UserId = new("a7000000-0000-0000-0000-000000000001");

    // Answers every lookup with the row stored under Stored, as SQL Server would for the twins.
    public class FoldingProxy : DispatchProxy
    {
        public IEmailVerificationStore Inner { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(IEmailVerificationStore.FindAccountStateAsync))
            {
                args![0] = Stored;
            }

            return targetMethod.Invoke(Inner, args);
        }
    }

    private static IEmailVerificationStore Fold(IEmailVerificationStore inner)
    {
        var proxy = DispatchProxy.Create<IEmailVerificationStore, FoldingProxy>();
        ((FoldingProxy)(object)proxy).Inner = inner;
        return proxy;
    }

    [Theory]
    [InlineData(Twin)]
    [InlineData(Fullwidth)]
    public async Task Forgot_For_A_Collation_Equal_Address_Sends_Nothing_And_Issues_Nothing(string requested)
    {
        using var db = new SqliteTestDatabase();
        await db.SeedAsync(TestData.User(UserId, Stored, isEmailConfirmed: true));
        await using var context = db.CreateContext();
        var h = new AuthHarness(context, decorateStore: Fold);

        var result = await h.Auth.ForgotPasswordAsync(new ForgotPasswordRequest { Email = requested });

        Assert.True(result.IsSuccess);
        Assert.Empty(h.Sender.Messages);
        Assert.Empty(await context.UserTokens.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Forgot_For_The_Exact_Address_Still_Sends_To_The_Stored_Address()
    {
        using var db = new SqliteTestDatabase();
        await db.SeedAsync(TestData.User(UserId, Stored, isEmailConfirmed: true));
        await using var context = db.CreateContext();
        var h = new AuthHarness(context, decorateStore: Fold);

        var result = await h.Auth.ForgotPasswordAsync(new ForgotPasswordRequest { Email = $" {Stored.ToUpperInvariant()} " });

        Assert.True(result.IsSuccess);
        Assert.Equal(Stored, Assert.Single(h.Sender.Messages).To);
        Assert.Single(await context.UserTokens.AsNoTracking().ToListAsync());
    }

    [Theory]
    [InlineData(Twin)]
    [InlineData(Fullwidth)]
    public async Task Resend_For_A_Collation_Equal_Address_Sends_Nothing_And_Rotates_Nothing(string requested)
    {
        using var db = new SqliteTestDatabase();
        await db.SeedAsync(TestData.User(UserId, Stored, isEmailConfirmed: false));
        await using var context = db.CreateContext();
        var h = new AuthHarness(context, decorateStore: Fold);

        var result = await h.Auth.ResendVerificationAsync(new ResendVerificationRequest { Email = requested });

        Assert.True(result.IsSuccess);
        Assert.Empty(h.Sender.Messages);
        Assert.Empty(await context.UserTokens.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Resend_For_The_Exact_Address_Still_Sends_To_The_Stored_Address()
    {
        using var db = new SqliteTestDatabase();
        await db.SeedAsync(TestData.User(UserId, Stored, isEmailConfirmed: false));
        await using var context = db.CreateContext();
        var h = new AuthHarness(context, decorateStore: Fold);

        var result = await h.Auth.ResendVerificationAsync(new ResendVerificationRequest { Email = $" {Stored.ToUpperInvariant()} " });

        Assert.True(result.IsSuccess);
        Assert.Equal(Stored, Assert.Single(h.Sender.Messages).To);
    }

    // Counts the replacement attempts, to prove the twin never reaches the write.
    public class ReplaceCountingProxy : DispatchProxy
    {
        public IEmailVerificationStore Inner { get; set; } = null!;
        public int ReplaceCalls { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(IEmailVerificationStore.TryReplacePendingAsync))
            {
                ReplaceCalls++;
            }

            if (targetMethod.Name == nameof(IEmailVerificationStore.FindAccountStateAsync))
            {
                args![0] = Stored;
            }

            return targetMethod.Invoke(Inner, args);
        }
    }

    [Theory]
    [InlineData(Twin)]
    [InlineData(Fullwidth)]
    public async Task Register_For_A_Collation_Equal_Address_Is_Duplicate_And_Replaces_Nothing(string requested)
    {
        using var db = new SqliteTestDatabase();
        await db.SeedAsync(TestData.User(UserId, Stored, isEmailConfirmed: false, passwordHash: "victim-hash"));
        await using var context = db.CreateContext();
        ReplaceCountingProxy? counter = null;
        var h = new AuthHarness(context, decorateStore: inner =>
        {
            var proxy = DispatchProxy.Create<IEmailVerificationStore, ReplaceCountingProxy>();
            counter = (ReplaceCountingProxy)(object)proxy;
            counter.Inner = inner;
            return proxy;
        });

        var result = await h.Auth.RegisterAsync(AuthHarness.Register(requested));

        Assert.False(result.IsSuccess);
        Assert.Equal(EmailVerificationService.ErrorCodes.DuplicateEmail, result.Error!.Code);
        Assert.Equal(0, counter!.ReplaceCalls);
        Assert.Empty(h.Sender.Messages);
        Assert.Empty(await context.UserTokens.AsNoTracking().ToListAsync());
        var row = await context.Users.AsNoTracking().SingleAsync();
        Assert.Equal("victim-hash", row.PasswordHash);
        Assert.Equal(Stored, row.Email);
    }

    [Fact]
    public async Task Register_For_The_Exact_Address_Still_Replaces_The_Pending_Row()
    {
        using var db = new SqliteTestDatabase();
        await db.SeedAsync(TestData.User(UserId, Stored, isEmailConfirmed: false, passwordHash: "old-hash"));
        await using var context = db.CreateContext();
        var h = new AuthHarness(context, decorateStore: Fold);

        var result = await h.Auth.RegisterAsync(AuthHarness.Register($" {Stored.ToUpperInvariant()} "));

        Assert.True(result.IsSuccess);
        Assert.Equal(Stored, Assert.Single(h.Sender.Messages).To);
        var row = await context.Users.AsNoTracking().SingleAsync();
        Assert.NotEqual("old-hash", row.PasswordHash);
        Assert.Equal(Stored, row.Email);
    }

    [SqlServerFact]
    public async Task On_Real_SQL_Server_Register_With_The_Twin_Leaves_The_Pending_Victim_Untouched()
    {
        using var db = new SqlServerTestDatabase();
        await db.SeedAsync(TestData.User(UserId, Stored, isEmailConfirmed: false, passwordHash: "victim-hash", firstName: "Victim"));

        await using var context = db.CreateContext();
        var h = new AuthHarness(context);

        // Register once with the exact address to get a latest token on record, then move the clock
        // past the cooldown so only the twin check can stop the second call.
        Assert.True((await h.Auth.RegisterAsync(AuthHarness.Register(Stored, firstName: "Victim"))).IsSuccess);
        var before = await context.Users.AsNoTracking().SingleAsync();
        var tokensBefore = await context.UserTokens.AsNoTracking().Select(t => t.Id).ToListAsync();
        h.Sender.Clear();
        h.Clock.Advance(TimeSpan.FromMinutes(5));

        var result = await h.Auth.RegisterAsync(AuthHarness.Register(Twin, password: "AttackerPassw0rd", firstName: "Attacker"));

        Assert.False(result.IsSuccess);
        Assert.Equal(EmailVerificationService.ErrorCodes.DuplicateEmail, result.Error!.Code);
        Assert.Empty(h.Sender.Messages);

        var after = await context.Users.AsNoTracking().SingleAsync();
        Assert.Equal(before.Email, after.Email);
        Assert.Equal(before.PasswordHash, after.PasswordHash);
        Assert.Equal(before.FirstName, after.FirstName);
        Assert.Equal(before.LastName, after.LastName);
        Assert.Equal(before.PhoneNumber, after.PhoneNumber);
        Assert.Equal(before.HomeLatitude, after.HomeLatitude);
        Assert.Equal(tokensBefore.OrderBy(i => i), (await context.UserTokens.AsNoTracking().Select(t => t.Id).ToListAsync()).OrderBy(i => i));
    }

    [SqlServerFact]
    public async Task On_Real_SQL_Server_The_Twin_Matches_The_Row_But_Forgot_And_Resend_Send_Nothing()
    {
        using var db = new SqlServerTestDatabase();
        await db.SeedAsync(TestData.User(UserId, Stored, isEmailConfirmed: true));
        var pendingId = new Guid("a7000000-0000-0000-0000-000000000002");
        const string pendingStored = "pending@business.example";
        await db.SeedAsync(TestData.User(pendingId, pendingStored, isEmailConfirmed: false));

        await using var context = db.CreateContext();
        var h = new AuthHarness(context);

        // The premise: the real collation does match the twin.
        var matched = await h.Store.FindAccountStateAsync(Twin);
        Assert.NotNull(matched);
        Assert.Equal(Stored, matched!.Email);

        Assert.True((await h.Auth.ForgotPasswordAsync(new ForgotPasswordRequest { Email = Twin })).IsSuccess);
        Assert.True((await h.Auth.ResendVerificationAsync(
            new ResendVerificationRequest { Email = "pending@busineß.example" })).IsSuccess);

        Assert.Empty(h.Sender.Messages);
        Assert.Empty(await context.UserTokens.AsNoTracking().ToListAsync());

        // The exact address still works and goes to the stored address.
        Assert.True((await h.Auth.ForgotPasswordAsync(new ForgotPasswordRequest { Email = Stored })).IsSuccess);
        Assert.Equal(Stored, Assert.Single(h.Sender.Messages).To);
    }
}
