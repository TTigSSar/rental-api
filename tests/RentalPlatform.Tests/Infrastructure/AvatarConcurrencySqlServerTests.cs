using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.DTOs;
using RentalPlatform.Application.Services;
using RentalPlatform.Infrastructure.Persistence;
using RentalPlatform.Infrastructure.Services;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Infrastructure;

// ADR-070 (C-*) against a REAL SQL Server: the conditional UPDATE in both SQL forms, and the races between
// uploads, a block and an account deletion with REAL files on disk. [SqlServerFact] skips with a reason when
// no SQL Server is reachable and fails when a declared one is down (see SqlServerFactAttribute).
[Collection(SqlServerConcurrencyCollection.Name)]
public sealed class AvatarConcurrencySqlServerTests : IDisposable
{
    private static readonly Guid UserId = new("c6000000-0000-0000-0000-000000000001");
    private static string Url(string name) => $"/uploads/avatars/{UserId:N}/{new string(name[0], 32)}.jpg";

    private readonly string _contentRoot;

    public AvatarConcurrencySqlServerTests()
    {
        _contentRoot = Path.Combine(Path.GetTempPath(), "rental-avatar-race-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_contentRoot, "wwwroot"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_contentRoot))
        {
            Directory.Delete(_contentRoot, recursive: true);
        }
    }

    private LocalFileStorageService Storage() =>
        new(new FakeHostEnvironment("Test") { ContentRootPath = _contentRoot }, Options.Create(new LocalFileStorageOptions()));

    private string UserDir => Path.Combine(_contentRoot, "wwwroot", "uploads", "avatars", UserId.ToString("N"));

    private AvatarService NewService(AppDbContext context, IFileStorageService storage) =>
        new(new FakeCurrentUserContext(UserId), new UserAuthStore(context), new AvatarStore(context), new FakeAvatarImageProcessor(), storage);

    private static AvatarContent Content() => new(new MemoryStream(new byte[] { 1, 2, 3, 4 }), 4);

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

    private static async Task<string?> StoredAsync(SqlServerTestDatabase db)
    {
        await using var context = db.CreateContext();
        return (await context.Users.AsNoTracking().SingleAsync(user => user.Id == UserId)).AvatarUrl;
    }

    private static string FileNameOf(string url) => url[(url.LastIndexOf('/') + 1)..];

    // C-1: the IS NULL form, on real T-SQL.
    [SqlServerFact]
    public async Task C1_The_IS_NULL_Form_Commits_Once_And_Then_Reports_Mismatch()
    {
        using var db = new SqlServerTestDatabase();
        await db.SeedAsync(TestData.User(UserId, "c1@test.local"));
        await using var context = db.CreateContext();
        var store = new AvatarStore(context);

        Assert.Equal(AvatarCasOutcome.Committed, await store.TryReplaceAsync(UserId, null, Url("a")));
        Assert.Equal(AvatarCasOutcome.Mismatch, await store.TryReplaceAsync(UserId, null, Url("b")));
        Assert.Equal(Url("a"), await StoredAsync(db));
    }

    // C-2: the = @p form, including replace-to-null and the state classification.
    [SqlServerFact]
    public async Task C2_The_Equals_Form_Commits_On_A_Match_And_Classifies_Blocked_And_Deleted()
    {
        using var db = new SqlServerTestDatabase();
        var user = TestData.User(UserId, "c2@test.local");
        user.AvatarUrl = Url("a");
        await db.SeedAsync(user);
        await using var context = db.CreateContext();
        var store = new AvatarStore(context);

        Assert.Equal(AvatarCasOutcome.Mismatch, await store.TryReplaceAsync(UserId, Url("z"), Url("b")));
        Assert.Equal(AvatarCasOutcome.Committed, await store.TryReplaceAsync(UserId, Url("a"), Url("b")));
        Assert.Equal(AvatarCasOutcome.Committed, await store.TryReplaceAsync(UserId, Url("b"), null));
        Assert.Null(await StoredAsync(db));

        await context.Users.Where(u => u.Id == UserId).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsBlocked, true));
        Assert.Equal(AvatarCasOutcome.UserBlocked, await store.TryReplaceAsync(UserId, null, Url("c")));
        await context.Users.Where(u => u.Id == UserId).ExecuteUpdateAsync(s => s.SetProperty(u => u.DeletedAt, DateTime.UtcNow));
        Assert.Equal(AvatarCasOutcome.UserDeleted, await store.TryReplaceAsync(UserId, null, Url("c")));
    }

    // C-3: an upload racing the deletion's commit and wipe. Whatever the interleaving, nothing survives.
    [SqlServerFact]
    public async Task C3_An_Upload_Racing_The_Account_Deletion_Leaves_No_Avatar_File()
    {
        for (var round = 0; round < 8; round++)
        {
            using var db = new SqlServerTestDatabase();
            var user = TestData.User(UserId, $"c3.{round}@test.local");
            user.AvatarUrl = Url("a");
            await db.SeedAsync(user);
            var storage = Storage();
            await storage.SaveAvatarAsync(new MemoryStream(new byte[] { 1 }), UserId);

            var results = await RaceAsync<object?>(2, async i =>
            {
                await using var context = db.CreateContext();
                if (i == 0)
                {
                    return await NewService(context, storage).UploadAsync(Content());
                }

                // What AccountDeletionService does: conditional commit, then the best-effort wipe.
                await context.Users.Where(u => u.Id == UserId)
                    .ExecuteUpdateAsync(s => s.SetProperty(u => u.DeletedAt, DateTime.UtcNow).SetProperty(u => u.AvatarUrl, (string?)null));
                await storage.DeleteAvatarDirectoryAsync(UserId);
                return null;
            });

            var upload = (Application.Common.ServiceResult<CurrentUserResponse>)results[0]!;
            // Upload either committed before the deletion (the wipe removed its file) or lost to it (401, it wiped).
            Assert.True(upload.IsSuccess || upload.Error!.Code == "auth.unauthenticated", upload.Error?.Code);
            Assert.False(Directory.Exists(UserDir) && Directory.GetFiles(UserDir).Length > 0,
                $"round {round}: an avatar file of a deleted account survived");
            Assert.Null(await StoredAsync(db));
        }
    }

    // C-4: concurrent uploads converge on one stored value, its file exists, every other file is gone.
    [SqlServerFact]
    public async Task C4_Concurrent_Uploads_Converge_On_One_File_And_Never_Delete_The_Current_One()
    {
        using var db = new SqlServerTestDatabase();
        var user = TestData.User(UserId, "c4@test.local");
        user.AvatarUrl = Url("a");
        await db.SeedAsync(user);
        var storage = Storage();
        Directory.CreateDirectory(UserDir);
        await File.WriteAllBytesAsync(Path.Combine(UserDir, FileNameOf(Url("a"))), new byte[] { 1 });

        var results = await RaceAsync(4, async _ =>
        {
            await using var context = db.CreateContext();
            return await NewService(context, storage).UploadAsync(Content());
        });

        // Three attempts always suffice for four racers here only if each loss is followed by a win; a loser that
        // runs out is a clean 409 and must have cleaned its own file.
        Assert.All(results, r => Assert.True(r.IsSuccess || r.Error!.Code == "auth.avatar_conflict"));
        Assert.Contains(results, r => r.IsSuccess);
        var stored = await StoredAsync(db);
        Assert.NotEqual(Url("a"), stored);
        var files = Directory.GetFiles(UserDir).Select(Path.GetFileName).ToArray();
        Assert.Equal(new[] { FileNameOf(stored!) }, files);
    }

    // C-5: a block racing an upload. Either the upload committed first (file present) or it saw the block (no file).
    [SqlServerFact]
    public async Task C5_A_Block_Racing_An_Upload_Never_Leaves_A_File_Without_A_Row_Pointing_At_It()
    {
        for (var round = 0; round < 8; round++)
        {
            using var db = new SqlServerTestDatabase();
            await db.SeedAsync(TestData.User(UserId, $"c5.{round}@test.local"));
            var storage = Storage();

            var results = await RaceAsync<object?>(2, async i =>
            {
                await using var context = db.CreateContext();
                if (i == 0)
                {
                    return await NewService(context, storage).UploadAsync(Content());
                }

                await context.Users.Where(u => u.Id == UserId).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsBlocked, true));
                return null;
            });

            var upload = (Application.Common.ServiceResult<CurrentUserResponse>)results[0]!;
            var stored = await StoredAsync(db);
            var files = Directory.Exists(UserDir) ? Directory.GetFiles(UserDir).Select(Path.GetFileName).ToArray() : Array.Empty<string?>();
            if (upload.IsSuccess)
            {
                Assert.Equal(new[] { FileNameOf(stored!) }, files);
            }
            else
            {
                Assert.Equal("auth.user_blocked", upload.Error!.Code);
                Assert.Null(stored);
                Assert.Empty(files);
            }

            if (Directory.Exists(UserDir))
            {
                Directory.Delete(UserDir, recursive: true);
            }
        }
    }

    // C-6: an upload that recreates the directory after the deletion wipe; the sweep removes it. Also proves the
    // sweep's batched user lookup translates on real SQL Server.
    [SqlServerFact]
    public async Task C6_A_Directory_Recreated_After_The_Wipe_Is_Removed_By_The_Sweep()
    {
        using var db = new SqlServerTestDatabase();
        var user = TestData.User(UserId, "c6@test.local");
        user.DeletedAt = DateTime.UtcNow;
        await db.SeedAsync(user);
        var storage = Storage();
        await storage.DeleteAvatarDirectoryAsync(UserId); // the deletion's wipe
        await storage.SaveAvatarAsync(new MemoryStream(new byte[] { 1 }), UserId); // a late save recreates it
        Assert.True(Directory.Exists(UserDir));

        var services = new ServiceCollection();
        services.AddScoped(_ => db.CreateContext());
        services.AddSingleton<IFileStorageService>(storage);
        var sweep = new AvatarOrphanSweepService(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System,
            NullLogger<AvatarOrphanSweepService>.Instance);

        var summary = await sweep.SweepAsync();

        Assert.False(Directory.Exists(UserDir));
        Assert.Equal(1, summary.DirectoriesWiped);
        Assert.Equal(0, summary.Failures);
    }
}
