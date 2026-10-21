using Microsoft.EntityFrameworkCore;
using RentalPlatform.Application.DTOs;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Infrastructure;

// The races ADR-035 section 2 (atomicity R1) exists for, against a REAL SQL Server: concurrent
// writers and real lock behaviour. [SqlServerFact] skips with a reason when no local SQL Server is
// reachable, and fails when the declared one is down (see SqlServerFactAttribute).
[Collection(RentalPlatform.Tests.TestSupport.SqlServerConcurrencyCollection.Name)]
public sealed class PasswordResetConcurrencyTests
{
    private const string Email = "race.reset@test.local";
    private const string OldPassword = "OldPassword123";
    private static readonly Guid UserId = new("a6000000-0000-0000-0000-000000000001");

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

    private static async Task<string> SeedAndIssueAsync(SqlServerTestDatabase db, FakeTimeProvider clock)
    {
        await db.SeedAsync(TestData.User(UserId, Email, passwordHash: $"hashed:{OldPassword}"));
        await using var context = db.CreateContext();
        var h = new AuthHarness(context, clock: clock);
        await h.Auth.ForgotPasswordAsync(new ForgotPasswordRequest { Email = Email });
        return h.Sender.LastTokenFor(Email)!;
    }

    [SqlServerFact]
    public async Task Two_Resets_With_One_Token_Exactly_One_Wins()
    {
        using var db = new SqlServerTestDatabase();
        var clock = new FakeTimeProvider(new DateTimeOffset(DateTime.UtcNow, TimeSpan.Zero));
        var token = await SeedAndIssueAsync(db, clock);

        var results = await RaceAsync(8, async i =>
        {
            await using var context = db.CreateContext();
            var harness = new AuthHarness(context, clock: clock);
            return await harness.Auth.ResetPasswordAsync(new ResetPasswordRequest { Token = token, NewPassword = $"Password-number-{i}" });
        });

        Assert.Equal(1, results.Count(result => result.IsSuccess));
        Assert.All(results.Where(result => !result.IsSuccess), result => Assert.Equal("auth.reset_token_invalid", result.Error!.Code));

        // The winner's password is the stored one; no loser left its hash behind.
        await using var verify = db.CreateContext();
        var stored = (await verify.Users.AsNoTracking().SingleAsync(user => user.Id == UserId)).PasswordHash;
        var winner = results.Select((result, i) => (result, i)).Single(pair => pair.result.IsSuccess).i;
        Assert.Equal($"hashed:Password-number-{winner}", stored);
        Assert.NotNull((await verify.UserTokens.AsNoTracking().SingleAsync()).ConsumedAt);
    }

    [SqlServerFact]
    public async Task A_New_Forgot_Request_Racing_A_Reset_Leaves_Exactly_One_Consistent_Outcome()
    {
        using var db = new SqlServerTestDatabase();
        var clock = new FakeTimeProvider(new DateTimeOffset(DateTime.UtcNow, TimeSpan.Zero));
        var token = await SeedAndIssueAsync(db, clock);
        clock.Advance(TimeSpan.FromSeconds(61)); // past the cooldown, so the forgot rotates the token

        await Task.WhenAll(
            Task.Run(async () =>
            {
                await using var context = db.CreateContext();
                return await new AuthHarness(context, clock: clock).Auth.ResetPasswordAsync(
                    new ResetPasswordRequest { Token = token, NewPassword = "RacingNewPassword1" });
            }),
            Task.Run(async () =>
            {
                await using var context = db.CreateContext();
                return await new AuthHarness(context, clock: clock).Auth.ForgotPasswordAsync(
                    new ForgotPasswordRequest { Email = Email });
            }));

        // Either order is legal, but never a deadlock/500 and never two live tokens, and the old token
        // can be used at most once.
        await using var verify = db.CreateContext();
        var tokens = await verify.UserTokens.AsNoTracking().Where(t => t.Purpose == TokenPurpose.PasswordReset).ToListAsync();
        Assert.True(tokens.Count(t => t.ConsumedAt == null) <= 1);
        Assert.NotNull(tokens.Single(t => t.CreatedAt < clock.GetUtcNow().UtcDateTime.AddSeconds(-30)).ConsumedAt);
    }
}
