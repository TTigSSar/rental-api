using Microsoft.EntityFrameworkCore;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.DTOs;
using RentalPlatform.Application.Services;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Infrastructure.Persistence;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Auth;

// ADR-070 at the service level (B-U*): the REAL AvatarService, AvatarStore and UserAuthStore over SQLite,
// with the image processor and the file storage as scriptable doubles. Interleavings are forced with
// HookAvatarStore, so every outcome is deterministic.
public sealed class AvatarServiceTests
{
    private static readonly Guid UserId = new("a9000000-0000-0000-0000-000000000001");
    private static readonly Guid OtherId = new("a9000000-0000-0000-0000-000000000002");
    private static string Url(Guid user, string name) => $"/uploads/avatars/{user:N}/{name}.jpg";
    private static readonly string UrlA = Url(UserId, new string('a', 32));
    private static readonly string UrlB = Url(UserId, new string('b', 32));
    private const string GoogleUrl = "https://lh3.googleusercontent.com/a/photo";

    private sealed class Harness : IAsyncDisposable
    {
        public Harness(SqliteTestDatabase db, Guid? currentUserId = null)
        {
            Context = db.CreateContext();
            var real = new AvatarStore(Context);
            Store = new HookAvatarStore(real);
            Service = new AvatarService(
                new FakeCurrentUserContext(currentUserId ?? UserId),
                new UserAuthStore(Context),
                Store,
                Processor,
                Storage);
        }

        public AppDbContext Context { get; }
        public HookAvatarStore Store { get; }
        public FakeAvatarImageProcessor Processor { get; } = new();
        public FakeFileStorageService Storage { get; } = new();
        public AvatarService Service { get; }

        public ValueTask DisposeAsync() => Context.DisposeAsync();
    }

    private static AvatarContent Content(long length = 4) => new(new MemoryStream(new byte[Math.Max(length, 1)]), length);

    private static async Task SeedUserAsync(SqliteTestDatabase db, string? avatarUrl = null, bool blocked = false, bool deleted = false)
    {
        var user = TestData.User(UserId, "avatar.user@test.local", isBlocked: blocked);
        user.AvatarUrl = avatarUrl;
        user.DeletedAt = deleted ? DateTime.UtcNow : null;
        await db.SeedAsync(user);
    }

    private static async Task<string?> StoredUrlAsync(SqliteTestDatabase db)
    {
        await using var context = db.CreateContext();
        return (await context.Users.AsNoTracking().SingleAsync(user => user.Id == UserId)).AvatarUrl;
    }

    // ---- happy paths --------------------------------------------------------------------------------------

    [Fact]
    public async Task BU1_First_Upload_Stores_The_New_Url_Returns_It_And_Deletes_Nothing()
    {
        using var db = new SqliteTestDatabase();
        await SeedUserAsync(db);
        await using var h = new Harness(db);

        var result = await h.Service.UploadAsync(Content());

        Assert.True(result.IsSuccess);
        var saved = Assert.Single(h.Storage.SavedAvatarUrls);
        Assert.Equal(saved, result.Value!.AvatarUrl);
        Assert.Equal(saved, await StoredUrlAsync(db));
        Assert.Empty(h.Storage.DeletedAvatars);
    }

    [Fact]
    public async Task BU2_Replace_Deletes_Only_The_Committed_Old_Managed_File()
    {
        using var db = new SqliteTestDatabase();
        await SeedUserAsync(db, UrlA);
        await using var h = new Harness(db);

        var result = await h.Service.UploadAsync(Content());

        Assert.True(result.IsSuccess);
        var saved = Assert.Single(h.Storage.SavedAvatarUrls);
        Assert.Equal(saved, await StoredUrlAsync(db));
        var deleted = Assert.Single(h.Storage.DeletedAvatars);
        Assert.Equal((UserId, UrlA), deleted);
    }

    [Fact]
    public async Task BU3_Replacing_A_Google_Avatar_Hands_The_External_Url_To_The_Storage_Which_Ignores_It()
    {
        using var db = new SqliteTestDatabase();
        await SeedUserAsync(db, GoogleUrl);
        await using var h = new Harness(db);

        Assert.True((await h.Service.UploadAsync(Content())).IsSuccess);

        // The service never decides what is "managed"; the real storage no-ops on an external URL (S-tests).
        Assert.Equal((UserId, GoogleUrl), Assert.Single(h.Storage.DeletedAvatars));
    }

    [Fact]
    public async Task BU4_Remove_Clears_The_Url_Deletes_The_Old_File_And_Returns_A_Null_AvatarUrl()
    {
        using var db = new SqliteTestDatabase();
        await SeedUserAsync(db, UrlA);
        await using var h = new Harness(db);

        var result = await h.Service.RemoveAsync();

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value!.AvatarUrl);
        Assert.Null(await StoredUrlAsync(db));
        Assert.Equal((UserId, UrlA), Assert.Single(h.Storage.DeletedAvatars));
    }

    [Fact]
    public async Task BU5_Remove_Without_An_Avatar_Is_An_Idempotent_200_That_Writes_And_Deletes_Nothing()
    {
        using var db = new SqliteTestDatabase();
        await SeedUserAsync(db);
        await using var h = new Harness(db);

        var result = await h.Service.RemoveAsync();

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value!.AvatarUrl);
        Assert.Empty(h.Store.Attempts);
        Assert.Empty(h.Storage.DeletedAvatars);
    }

    [Fact]
    public async Task BU6_The_Tracked_Entity_Is_Not_Left_Modified_After_The_Commit()
    {
        using var db = new SqliteTestDatabase();
        await SeedUserAsync(db, UrlA);
        await using var h = new Harness(db);

        Assert.True((await h.Service.UploadAsync(Content())).IsSuccess);

        var user = h.Context.ChangeTracker.Entries<User>().Single();
        Assert.Equal(EntityState.Unchanged, user.State);
        Assert.Equal(await StoredUrlAsync(db), user.Entity.AvatarUrl);
    }

    // ---- no Notifications UPDATE (ADR-070 point 8) -----------------------------------------------------------

    [Fact]
    public async Task BU12_Upload_And_Remove_Never_Modify_A_Notification_Snapshot()
    {
        using var db = new SqliteTestDatabase();
        await SeedUserAsync(db, UrlA);
        var recipient = TestData.User(OtherId, "recipient@test.local");
        var notification = new Notification
        {
            Id = Guid.NewGuid(),
            RecipientId = OtherId,
            Kind = NotificationKind.Request,
            Category = NotificationCategory.Booking,
            Title = "t",
            Body = "b",
            ActorName = "Avatar User",
            ActorAvatarUrl = UrlA,
            EntityId = Guid.NewGuid(),
            DeepLink = "/bookings/x",
            CreatedAt = DateTime.UtcNow
        };
        await db.SeedAsync(recipient, notification);
        await using var h = new Harness(db);

        Assert.True((await h.Service.UploadAsync(Content())).IsSuccess);
        Assert.True((await h.Service.RemoveAsync()).IsSuccess);

        await using var verify = db.CreateContext();
        Assert.Equal(UrlA, (await verify.Notifications.AsNoTracking().SingleAsync()).ActorAvatarUrl);
    }

    // ---- guards ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task BU7_No_Current_User_Is_401_And_Nothing_Is_Processed()
    {
        using var db = new SqliteTestDatabase();
        await using var h = new Harness(db, currentUserId: Guid.Empty);

        var result = await h.Service.UploadAsync(Content());

        Assert.Equal("auth.unauthenticated", result.Error!.Code);
        Assert.Equal(0, h.Processor.Calls);
        Assert.Empty(h.Storage.SavedAvatarUrls);
    }

    [Fact]
    public async Task BU8_A_Blocked_User_Is_403_On_Upload_And_On_Remove_And_Nothing_Is_Written()
    {
        using var db = new SqliteTestDatabase();
        await SeedUserAsync(db, UrlA, blocked: true);
        await using var h = new Harness(db);

        Assert.Equal("auth.user_blocked", (await h.Service.UploadAsync(Content())).Error!.Code);
        Assert.Equal("auth.user_blocked", (await h.Service.RemoveAsync()).Error!.Code);

        Assert.Equal(0, h.Processor.Calls);
        Assert.Empty(h.Storage.SavedAvatarUrls);
        Assert.Equal(UrlA, await StoredUrlAsync(db));
    }

    [Fact]
    public async Task BU37_An_Already_Deleted_User_Is_401_And_No_File_Is_Written()
    {
        using var db = new SqliteTestDatabase();
        await SeedUserAsync(db, deleted: true);
        await using var h = new Harness(db);

        var result = await h.Service.UploadAsync(Content());

        Assert.Equal("auth.unauthenticated", result.Error!.Code);
        Assert.Empty(h.Storage.SavedAvatarUrls);
        Assert.Equal(0, h.Processor.Calls);
    }

    [Fact]
    public async Task BU9_No_File_Is_avatar_missing()
    {
        using var db = new SqliteTestDatabase();
        await SeedUserAsync(db);
        await using var h = new Harness(db);

        Assert.Equal("auth.avatar_missing", (await h.Service.UploadAsync(null)).Error!.Code);
    }

    [Fact]
    public async Task BU10_A_Zero_Length_File_Is_avatar_empty_Before_Processing()
    {
        using var db = new SqliteTestDatabase();
        await SeedUserAsync(db);
        await using var h = new Harness(db);

        var result = await h.Service.UploadAsync(new AvatarContent(new MemoryStream(), 0));

        Assert.Equal("auth.avatar_empty", result.Error!.Code);
        Assert.Equal(0, h.Processor.Calls);
    }

    [Theory]
    [InlineData(AvatarProcessingStatus.InvalidType, "auth.avatar_invalid_type")]
    [InlineData(AvatarProcessingStatus.TooLarge, "auth.avatar_too_large")]
    [InlineData(AvatarProcessingStatus.Busy, "auth.avatar_busy")]
    public async Task BU11_Processor_Verdicts_Map_To_Error_Codes_And_Save_Nothing(AvatarProcessingStatus status, string code)
    {
        using var db = new SqliteTestDatabase();
        await SeedUserAsync(db, UrlA);
        await using var h = new Harness(db);
        h.Processor.Status = status;

        var result = await h.Service.UploadAsync(Content());

        Assert.Equal(code, result.Error!.Code);
        Assert.Equal(status == AvatarProcessingStatus.Busy ? 5 : null, result.Error.RetryAfterSeconds);
        Assert.Empty(h.Storage.SavedAvatarUrls);
        Assert.Equal(UrlA, await StoredUrlAsync(db));
    }

    // ---- CAS (B-U34 and friends) -----------------------------------------------------------------------------

    [Fact]
    public async Task BU34_Attempt_1_Loses_Attempt_2_Wins_Only_The_Value_It_Committed_Over_Is_Deleted()
    {
        using var db = new SqliteTestDatabase();
        await SeedUserAsync(db, UrlA);
        await using var h = new Harness(db);
        h.Store.BeforeTryReplace = async attempt =>
        {
            if (attempt == 1)
            {
                // A rival request replaced A with B between our read and our update.
                await using var rival = db.CreateContext();
                await rival.Users.Where(user => user.Id == UserId).ExecuteUpdateAsync(s => s.SetProperty(user => user.AvatarUrl, UrlB));
            }
        };

        var result = await h.Service.UploadAsync(Content());

        Assert.True(result.IsSuccess);
        Assert.Equal(new[] { UrlA, UrlB }, h.Store.Attempts.Select(a => a.ExpectedOld).ToArray());
        var deleted = Assert.Single(h.Storage.DeletedAvatars);
        Assert.Equal(UrlB, deleted.Url); // never A: A was already gone when B was the value
        Assert.Equal(Assert.Single(h.Storage.SavedAvatarUrls), await StoredUrlAsync(db));
    }

    [Fact]
    public async Task BU34b_Three_Lost_Attempts_Are_409_The_New_File_Is_Deleted_And_No_Old_File_Is_Touched()
    {
        using var db = new SqliteTestDatabase();
        await SeedUserAsync(db, UrlA);
        await using var h = new Harness(db);
        var flip = 0;
        h.Store.BeforeTryReplace = async _ =>
        {
            await using var rival = db.CreateContext();
            var next = Url(UserId, new string((char)('c' + flip++), 32));
            await rival.Users.Where(user => user.Id == UserId).ExecuteUpdateAsync(s => s.SetProperty(user => user.AvatarUrl, next));
        };

        var result = await h.Service.UploadAsync(Content());

        Assert.Equal("auth.avatar_conflict", result.Error!.Code);
        Assert.Equal(AvatarService.MaxCasAttempts, h.Store.Attempts.Count);
        var saved = Assert.Single(h.Storage.SavedAvatarUrls);
        Assert.Equal((UserId, saved), Assert.Single(h.Storage.DeletedAvatars));
    }

    [Fact]
    public async Task BU13_The_Account_Is_Deleted_While_We_Work_401_The_Directory_Is_Wiped()
    {
        using var db = new SqliteTestDatabase();
        await SeedUserAsync(db, UrlA);
        await using var h = new Harness(db);
        h.Store.BeforeTryReplace = async _ =>
        {
            await using var rival = db.CreateContext();
            await rival.Users.Where(user => user.Id == UserId).ExecuteUpdateAsync(s => s.SetProperty(user => user.DeletedAt, DateTime.UtcNow));
        };

        var result = await h.Service.UploadAsync(Content());

        Assert.Equal("auth.unauthenticated", result.Error!.Code);
        Assert.Equal(new[] { UserId }, h.Storage.DeletedAvatarDirectories);
        Assert.Equal(UrlA, await StoredUrlAsync(db)); // the CAS wrote nothing
    }

    [Fact]
    public async Task BU14_The_User_Is_Blocked_While_We_Work_403_And_The_New_File_Is_Deleted()
    {
        using var db = new SqliteTestDatabase();
        await SeedUserAsync(db, UrlA);
        await using var h = new Harness(db);
        h.Store.BeforeTryReplace = async _ =>
        {
            await using var rival = db.CreateContext();
            await rival.Users.Where(user => user.Id == UserId).ExecuteUpdateAsync(s => s.SetProperty(user => user.IsBlocked, true));
        };

        var result = await h.Service.UploadAsync(Content());

        Assert.Equal("auth.user_blocked", result.Error!.Code);
        var saved = Assert.Single(h.Storage.SavedAvatarUrls);
        Assert.Equal((UserId, saved), Assert.Single(h.Storage.DeletedAvatars));
        Assert.Equal(UrlA, await StoredUrlAsync(db));
    }

    [Fact]
    public async Task BU15_An_Ambiguous_Commit_Keeps_The_New_File_Deletes_Nothing_And_Is_409()
    {
        using var db = new SqliteTestDatabase();
        await SeedUserAsync(db, UrlA);
        await using var h = new Harness(db);
        h.Store.ForcedOutcome = _ => AvatarCasOutcome.CommitAmbiguous;

        var result = await h.Service.UploadAsync(Content());

        Assert.Equal("auth.avatar_conflict", result.Error!.Code);
        Assert.Single(h.Storage.SavedAvatarUrls);
        Assert.Empty(h.Storage.DeletedAvatars);
        Assert.Empty(h.Storage.DeletedAvatarDirectories);
    }

    [Fact]
    public async Task BU16_A_Failing_Old_File_Delete_Does_Not_Fail_The_Committed_Upload()
    {
        using var db = new SqliteTestDatabase();
        await SeedUserAsync(db, UrlA);
        await using var h = new Harness(db);
        h.Storage.ThrowOnDeleteAvatar = true;

        var result = await h.Service.UploadAsync(Content());

        Assert.True(result.IsSuccess);
        Assert.Equal(Assert.Single(h.Storage.SavedAvatarUrls), await StoredUrlAsync(db));
    }

    // ---- exceptions (B-U35) ----------------------------------------------------------------------------------

    [Fact]
    public async Task BU35_A_Client_Cancellation_In_The_Processor_Propagates_And_Leaves_No_File_Or_Row_Change()
    {
        using var db = new SqliteTestDatabase();
        await SeedUserAsync(db, UrlA);
        await using var h = new Harness(db);
        h.Processor.Throw = new OperationCanceledException();

        await Assert.ThrowsAsync<OperationCanceledException>(() => h.Service.UploadAsync(Content()));

        Assert.Empty(h.Storage.SavedAvatarUrls);
        Assert.Equal(UrlA, await StoredUrlAsync(db));
    }

    [Fact]
    public async Task BU35b_An_Io_Failure_On_Save_Propagates_Untranslated_So_It_Becomes_A_Logged_500()
    {
        using var db = new SqliteTestDatabase();
        await SeedUserAsync(db, UrlA);
        await using var h = new Harness(db);
        h.Storage.ThrowOnSaveAvatar = true;

        await Assert.ThrowsAsync<IOException>(() => h.Service.UploadAsync(Content()));

        Assert.Equal(UrlA, await StoredUrlAsync(db));
    }

    [Fact]
    public async Task BU35c_An_Unexpected_Store_Failure_Deletes_The_New_File_And_Propagates()
    {
        using var db = new SqliteTestDatabase();
        await SeedUserAsync(db, UrlA);
        await using var h = new Harness(db);
        h.Store.BeforeTryReplace = _ => throw new InvalidOperationException("db down");

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.UploadAsync(Content()));

        var saved = Assert.Single(h.Storage.SavedAvatarUrls);
        Assert.Equal((UserId, saved), Assert.Single(h.Storage.DeletedAvatars));
    }

    // ---- the two SQL forms ------------------------------------------------------------------------------------

    [Fact]
    public async Task BU20_The_Cas_Query_Has_Two_Forms_IS_NULL_And_Equals()
    {
        using var db = new SqliteTestDatabase();
        await using var context = db.CreateContext();

        var whenNull = AvatarStore.CasQuery(context, UserId, null).ToQueryString();
        var whenSet = AvatarStore.CasQuery(context, UserId, UrlA).ToQueryString();

        Assert.Contains("\"AvatarUrl\" IS NULL", whenNull);
        Assert.DoesNotContain("IS NOT NULL", whenNull);
        Assert.Contains("\"AvatarUrl\" = ", whenSet);
        Assert.DoesNotContain("IS NULL", whenSet.Replace("\"DeletedAt\" IS NULL", string.Empty));
        foreach (var sql in new[] { whenNull, whenSet })
        {
            Assert.Contains("\"DeletedAt\" IS NULL", sql);
            Assert.Contains("\"IsBlocked\"", sql);
        }
    }

    [Fact]
    public async Task BU21_The_Real_Store_Reports_Mismatch_Deleted_And_Blocked()
    {
        using var db = new SqliteTestDatabase();
        await SeedUserAsync(db, UrlA);
        await using var context = db.CreateContext();
        var store = new AvatarStore(context);

        Assert.Equal(AvatarCasOutcome.Mismatch, await store.TryReplaceAsync(UserId, null, UrlB));
        Assert.Equal(AvatarCasOutcome.Mismatch, await store.TryReplaceAsync(UserId, UrlB, UrlA));
        Assert.Equal(AvatarCasOutcome.Committed, await store.TryReplaceAsync(UserId, UrlA, UrlB));
        Assert.Equal(AvatarCasOutcome.Committed, await store.TryReplaceAsync(UserId, UrlB, null));
        Assert.Equal(AvatarCasOutcome.Committed, await store.TryReplaceAsync(UserId, null, UrlA)); // the IS NULL form

        await context.Users.Where(user => user.Id == UserId).ExecuteUpdateAsync(s => s.SetProperty(user => user.IsBlocked, true));
        Assert.Equal(AvatarCasOutcome.UserBlocked, await store.TryReplaceAsync(UserId, UrlA, UrlB));
        await context.Users.Where(user => user.Id == UserId).ExecuteUpdateAsync(s => s.SetProperty(user => user.DeletedAt, DateTime.UtcNow));
        Assert.Equal(AvatarCasOutcome.UserDeleted, await store.TryReplaceAsync(UserId, UrlA, UrlB));
        Assert.Equal(AvatarCasOutcome.UserDeleted, await store.TryReplaceAsync(Guid.NewGuid(), null, UrlB)); // no such row
    }
}
