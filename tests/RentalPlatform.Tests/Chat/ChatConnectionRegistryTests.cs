using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RentalPlatform.Api.Hubs;
using RentalPlatform.Infrastructure.Services;
using RentalPlatform.Tests.TestSupport;
using Xunit;
using static RentalPlatform.Tests.TestSupport.SessionRevocationTestSupport;

namespace RentalPlatform.Tests.Chat;

// ADR-055 section 6: the registry, the revoker and the hub's connect-time re-check, with a stand-in for
// the SignalR connection (the transport itself is not under test; the decisions around it are).
public sealed class ChatConnectionRegistryTests
{
    private static readonly Guid UserA = new("d1000000-0000-0000-0000-000000000001");
    private static readonly Guid UserB = new("d1000000-0000-0000-0000-000000000002");

    private static ChatConnectionRegistry NewRegistry() => new(NullLogger<ChatConnectionRegistry>.Instance);

    private static ChatHub NewHub(ChatConnectionRegistry registry, UserSecurityStateCache cache, FakeHubCallerContext context) =>
        new(NullLogger<ChatHub>.Instance, registry, cache) { Context = context };

    private static async Task<(SqliteTestDatabase Db, UserSecurityStateCache Cache)> SeedAsync(
        Func<IUserSecurityStateReader, IUserSecurityStateReader>? decorate = null)
    {
        var db = new SqliteTestDatabase();
        await db.SeedAsync(TestData.User(UserA, "a@hub.local"), TestData.User(UserB, "b@hub.local"));
        IUserSecurityStateReader reader = new DbReader(db.CreateContext);
        return (db, NewCache(decorate is null ? reader : decorate(reader)));
    }

    private static async Task RotateAsync(SqliteTestDatabase db, Guid userId)
    {
        await using var context = db.CreateContext();
        await context.Users.Where(user => user.Id == userId)
            .ExecuteUpdateAsync(set => set.SetProperty(user => user.TokenVersion, user => user.TokenVersion + 1));
    }

    [Fact]
    public void Revoke_Aborts_Every_Connection_Of_The_User_And_Leaves_Others_Alone()
    {
        var registry = NewRegistry();
        var tab1 = new FakeHubCallerContext(UserA, "c1");
        var tab2 = new FakeHubCallerContext(UserA, "c2");
        var other = new FakeHubCallerContext(UserB, "c3");
        registry.Register(UserA, tab1);
        registry.Register(UserA, tab2);
        registry.Register(UserB, other);

        registry.AbortUser(UserA);

        Assert.Equal(1, tab1.AbortCount);
        Assert.Equal(1, tab2.AbortCount);
        Assert.Equal(0, other.AbortCount);
    }

    [Fact]
    public async Task A_Failing_Abort_Is_Isolated_Never_Thrown_And_The_Other_Connections_Are_Still_Aborted()
    {
        var (db, cache) = await SeedAsync();
        using var _ = db;
        var registry = NewRegistry();
        var broken = new FakeHubCallerContext(UserA, "c1", throwOnAbort: true);
        var fine = new FakeHubCallerContext(UserA, "c2");
        registry.Register(UserA, broken);
        registry.Register(UserA, fine);
        var revoker = new SessionRevoker(cache, registry);

        await revoker.RevokeAsync(UserA); // must not throw

        Assert.Equal(1, broken.AbortCount);
        Assert.Equal(1, fine.AbortCount);
    }

    [Fact]
    public void Unregister_Removes_The_Connection_And_Drops_The_Empty_Bucket()
    {
        var registry = NewRegistry();
        var one = new FakeHubCallerContext(UserA, "c1");
        registry.Register(UserA, one);
        registry.Register(UserA, new FakeHubCallerContext(UserA, "c2"));

        registry.Unregister(UserA, "c1");
        Assert.Single(registry.Snapshot(UserA));

        registry.Unregister(UserA, "c2");
        Assert.Empty(registry.Snapshot(UserA));

        // Registering again after the bucket was dropped works.
        registry.Register(UserA, one);
        Assert.Single(registry.Snapshot(UserA));
    }

    [Fact]
    public async Task A_Connection_Without_A_Parseable_User_Id_Is_Aborted()
    {
        var (db, cache) = await SeedAsync();
        using var _ = db;
        var context = new FakeHubCallerContext(UserA, "c1", TokenFor(UserA, 0), userIdentifier: "not-a-guid");

        await NewHub(NewRegistry(), cache, context).OnConnectedAsync();

        Assert.Equal(1, context.AbortCount);
    }

    [Fact]
    public async Task A_Current_Connection_Registers_Stays_Open_And_Is_Removed_On_Disconnect()
    {
        var (db, cache) = await SeedAsync();
        using var _ = db;
        var registry = NewRegistry();
        var context = new FakeHubCallerContext(UserA, "c1", TokenFor(UserA, 0));
        var hub = NewHub(registry, cache, context);

        await hub.OnConnectedAsync();
        Assert.Equal(0, context.AbortCount);
        Assert.Single(registry.Snapshot(UserA));

        await hub.OnDisconnectedAsync(null);
        Assert.Empty(registry.Snapshot(UserA));
    }

    [Fact]
    public async Task A_Connection_Authenticated_Before_A_Rotation_Is_Aborted_By_The_Recheck()
    {
        var (db, cache) = await SeedAsync();
        using var _ = db;
        var registry = NewRegistry();
        var revoker = new SessionRevoker(cache, registry);
        var staleToken = TokenFor(UserA, 0); // passed the handshake's authentication...

        await RotateAsync(db, UserA);        // ...then a password change committed and revoked (nothing to abort yet)
        await revoker.RevokeAsync(UserA);

        var context = new FakeHubCallerContext(UserA, "c1", staleToken);
        await NewHub(registry, cache, context).OnConnectedAsync();

        Assert.Equal(1, context.AbortCount);
    }

    // The closing of the connect-vs-rotation race: the connection is registered, its re-check is still
    // reading, and the rotation + abort sweep run in that window. The sweep finds the registered
    // connection even though the re-check then answers with the stale state.
    [Fact]
    public async Task A_Rotation_During_The_Recheck_Read_Is_Caught_By_The_Abort_Sweep()
    {
        GatedReader? gated = null;
        var (db, cache) = await SeedAsync(reader => gated = new GatedReader(reader));
        using var _ = db;
        var registry = NewRegistry();
        var revoker = new SessionRevoker(cache, registry);
        var context = new FakeHubCallerContext(UserA, "c1", TokenFor(UserA, 0));

        var connecting = NewHub(registry, cache, context).OnConnectedAsync();
        await gated!.ReadCompleted;           // registered; the re-check read the OLD version and is paused
        await RotateAsync(db, UserA);
        await revoker.RevokeAsync(UserA);     // sweep: finds the registered connection
        gated.Release();
        await connecting;

        Assert.True(context.AbortCount >= 1);
    }
}
