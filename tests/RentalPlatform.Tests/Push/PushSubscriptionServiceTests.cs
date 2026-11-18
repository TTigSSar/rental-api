using Microsoft.EntityFrameworkCore;
using RentalPlatform.Application.DTOs;
using RentalPlatform.Application.Services;
using RentalPlatform.Infrastructure.Persistence;
using RentalPlatform.Infrastructure.Services;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Push;

// ADR-067 section 7 against the REAL store on SQLite: upsert by endpoint hash, ownership move, the cap of 10.
public sealed class PushSubscriptionServiceTests : IDisposable
{
    private static readonly Guid UserA = new("e1000000-0000-0000-0000-000000000001");
    private static readonly Guid UserB = new("e1000000-0000-0000-0000-000000000002");

    private readonly SqliteTestDatabase _db = new();
    private readonly FakeTimeProvider _clock = new();

    public void Dispose() => _db.Dispose();

    private sealed class Settings : RentalPlatform.Application.Abstractions.IWebPushSettings
    {
        public bool IsEnabled { get; init; } = true;
        public string? PublicKey { get; init; } = "public-key";
    }

    private PushSubscriptionService ServiceFor(Guid? userId, AppDbContext context, bool enabled = true) =>
        new(new FakeCurrentUserContext(userId), new PushSubscriptionStore(context), new Settings { IsEnabled = enabled }, _clock);

    private static PushSubscriptionRequest Request(string endpoint, string? p256dh = null, string? auth = null) =>
        new() { Endpoint = endpoint, P256dh = p256dh ?? PushTestKeys.P256dh(), Auth = auth ?? PushTestKeys.Auth() };

    private async Task SeedUsersAsync()
    {
        await _db.SeedAsync(
            TestData.User(UserA, "a@push.local"),
            TestData.User(UserB, "b@push.local"));
    }

    [Fact]
    public async Task Subscribing_Twice_With_The_Same_Endpoint_Keeps_One_Row_With_The_Latest_Keys()
    {
        await SeedUsersAsync();
        var endpoint = PushTestKeys.FcmEndpoint();
        var newest = Request(endpoint);

        await using (var context = _db.CreateContext())
        {
            Assert.True((await ServiceFor(UserA, context).SubscribeAsync(Request(endpoint))).IsSuccess);
        }

        _clock.Advance(TimeSpan.FromMinutes(5));
        await using (var context = _db.CreateContext())
        {
            Assert.True((await ServiceFor(UserA, context).SubscribeAsync(newest)).IsSuccess);
        }

        await using var read = _db.CreateContext();
        var row = await read.PushSubscriptions.AsNoTracking().SingleAsync();
        Assert.Equal(UserA, row.UserId);
        Assert.Equal(newest.P256dh, row.P256dh);
        Assert.Equal(newest.Auth, row.Auth);
        Assert.Equal(32, row.EndpointHash.Length);
    }

    [Fact]
    public async Task An_Endpoint_Registered_By_Another_User_Moves_To_The_Caller()
    {
        await SeedUsersAsync();
        var endpoint = PushTestKeys.FcmEndpoint();

        await using (var context = _db.CreateContext())
        {
            await ServiceFor(UserA, context).SubscribeAsync(Request(endpoint));
        }

        await using (var context = _db.CreateContext())
        {
            Assert.True((await ServiceFor(UserB, context).SubscribeAsync(Request(endpoint))).IsSuccess);
        }

        await using var read = _db.CreateContext();
        var row = await read.PushSubscriptions.AsNoTracking().SingleAsync();
        Assert.Equal(UserB, row.UserId);
    }

    [Fact]
    public async Task The_Eleventh_Subscription_Evicts_The_Oldest_One()
    {
        await SeedUsersAsync();
        var endpoints = Enumerable.Range(0, 12).Select(_ => PushTestKeys.FcmEndpoint()).ToList();

        foreach (var endpoint in endpoints)
        {
            _clock.Advance(TimeSpan.FromMinutes(1));
            await using var context = _db.CreateContext();
            Assert.True((await ServiceFor(UserA, context).SubscribeAsync(Request(endpoint))).IsSuccess);
        }

        await using var read = _db.CreateContext();
        var kept = await read.PushSubscriptions.AsNoTracking().Where(row => row.UserId == UserA).Select(row => row.Endpoint).ToListAsync();
        Assert.Equal(PushSubscriptionService.MaxSubscriptionsPerUser, kept.Count);
        Assert.DoesNotContain(endpoints[0], kept);
        Assert.DoesNotContain(endpoints[1], kept);
        Assert.Contains(endpoints[11], kept);
    }

    [Fact]
    public async Task The_Cap_Is_Per_User()
    {
        await SeedUsersAsync();
        for (var index = 0; index < 10; index++)
        {
            _clock.Advance(TimeSpan.FromMinutes(1));
            await using var context = _db.CreateContext();
            await ServiceFor(UserA, context).SubscribeAsync(Request(PushTestKeys.FcmEndpoint()));
        }

        await using (var context = _db.CreateContext())
        {
            await ServiceFor(UserB, context).SubscribeAsync(Request(PushTestKeys.FcmEndpoint()));
        }

        await using var read = _db.CreateContext();
        Assert.Equal(10, await read.PushSubscriptions.CountAsync(row => row.UserId == UserA));
        Assert.Equal(1, await read.PushSubscriptions.CountAsync(row => row.UserId == UserB));
    }

    [Fact]
    public async Task Unsubscribe_Deletes_Only_The_Callers_Row_And_Is_Idempotent()
    {
        await SeedUsersAsync();
        var endpoint = PushTestKeys.FcmEndpoint();
        await using (var context = _db.CreateContext())
        {
            await ServiceFor(UserA, context).SubscribeAsync(Request(endpoint));
        }

        // Another user cannot remove it.
        await using (var context = _db.CreateContext())
        {
            var result = await ServiceFor(UserB, context).UnsubscribeAsync(new PushUnsubscribeRequest { Endpoint = endpoint });
            Assert.True(result.IsSuccess);
        }

        await using (var read = _db.CreateContext())
        {
            Assert.Equal(1, await read.PushSubscriptions.CountAsync());
        }

        await using (var context = _db.CreateContext())
        {
            Assert.True((await ServiceFor(UserA, context).UnsubscribeAsync(new PushUnsubscribeRequest { Endpoint = endpoint })).IsSuccess);
            Assert.True((await ServiceFor(UserA, context).UnsubscribeAsync(new PushUnsubscribeRequest { Endpoint = endpoint })).IsSuccess);
        }

        await using var final = _db.CreateContext();
        Assert.Equal(0, await final.PushSubscriptions.CountAsync());
    }

    [Fact]
    public async Task An_Invalid_Subscription_Is_Refused_With_The_Push_Error_Code_And_Nothing_Is_Stored()
    {
        await SeedUsersAsync();
        await using var context = _db.CreateContext();

        var result = await ServiceFor(UserA, context).SubscribeAsync(Request("https://evil.com/x"));

        Assert.False(result.IsSuccess);
        Assert.Equal("push.invalid_subscription", result.Error!.Code);
        Assert.Equal(0, await _db.CreateContext().PushSubscriptions.CountAsync());
    }

    [Fact]
    public void The_Public_Key_Is_Not_Found_When_Push_Is_Not_Configured()
    {
        using var context = _db.CreateContext();
        var off = ServiceFor(null, context, enabled: false).GetPublicKey();
        Assert.False(off.IsSuccess);
        Assert.Equal("push.not_configured", off.Error!.Code);

        Assert.Equal("public-key", ServiceFor(null, context).GetPublicKey().Value!.PublicKey);
    }
}
