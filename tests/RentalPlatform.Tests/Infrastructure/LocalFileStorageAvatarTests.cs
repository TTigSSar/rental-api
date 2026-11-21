using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Infrastructure.Services;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Infrastructure;

// ADR-070 storage (S-*): the REAL LocalFileStorageService on a temp content root (M-013: the fake never
// catches path-handling bugs - Windows separators, sibling-prefix containment, traversal).
public sealed class LocalFileStorageAvatarTests : IDisposable
{
    private static readonly Guid UserId = new("c1000000-0000-0000-0000-000000000001");
    private static readonly Guid OtherId = new("c1000000-0000-0000-0000-000000000002");

    private readonly string _contentRoot;
    private readonly ListLogger<LocalFileStorageService> _logger = new();

    public LocalFileStorageAvatarTests()
    {
        _contentRoot = Path.Combine(Path.GetTempPath(), "rental-avatar-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_contentRoot, "wwwroot"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_contentRoot))
        {
            Directory.Delete(_contentRoot, recursive: true);
        }
    }

    private LocalFileStorageService Create(string avatarsPath = "uploads/avatars") =>
        new(new FakeHostEnvironment("Test") { ContentRootPath = _contentRoot },
            Options.Create(new LocalFileStorageOptions { AvatarsPath = avatarsPath }),
            _logger);

    private string UserDir(Guid id) => Path.Combine(_contentRoot, "wwwroot", "uploads", "avatars", id.ToString("N"));

    private static MemoryStream Jpeg() => new(new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });

    private string PhysicalPath(string url) =>
        Path.Combine(_contentRoot, "wwwroot", url.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));

    // S-1
    [Fact]
    public async Task S1_Save_Writes_A_Guid_Named_Jpg_Under_The_Users_Directory_And_Returns_The_Url()
    {
        var url = await Create().SaveAvatarAsync(Jpeg(), UserId);

        Assert.Matches($"^/uploads/avatars/{UserId:N}/[0-9a-f]{{32}}\\.jpg$", url);
        Assert.True(File.Exists(PhysicalPath(url)));
        Assert.Equal(new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 }, await File.ReadAllBytesAsync(PhysicalPath(url)));
    }

    // S-2
    [Fact]
    public async Task S2_Every_Save_Gets_A_New_Url()
    {
        var service = Create();

        var first = await service.SaveAvatarAsync(Jpeg(), UserId);
        var second = await service.SaveAvatarAsync(Jpeg(), UserId);

        Assert.NotEqual(first, second);
        Assert.Equal(2, Directory.GetFiles(UserDir(UserId)).Length);
    }

    // S-3: the configured path may use forward slashes (the Windows half-normalised path bug of M-013).
    [Theory]
    [InlineData("uploads/avatars")]
    [InlineData("uploads\\avatars")]
    [InlineData("/uploads/avatars/")]
    [InlineData("media/people/avatars")]
    public async Task S3_A_Configured_Path_In_Any_Spelling_Saves_Resolves_And_Deletes(string configured)
    {
        var service = Create(configured);

        var url = await service.SaveAvatarAsync(Jpeg(), UserId);

        Assert.True(await service.DeleteAvatarAsync(UserId, url));
        Assert.False(File.Exists(PhysicalPath(url)));
    }

    // S-4
    [Fact]
    public async Task S4_Delete_Removes_Only_The_Callers_Own_File()
    {
        var service = Create();
        var own = await service.SaveAvatarAsync(Jpeg(), UserId);
        var foreign = await service.SaveAvatarAsync(Jpeg(), OtherId);

        Assert.False(await service.DeleteAvatarAsync(UserId, foreign)); // another user's URL
        Assert.True(File.Exists(PhysicalPath(foreign)));
        Assert.True(await service.DeleteAvatarAsync(UserId, own));
        Assert.False(File.Exists(PhysicalPath(own)));
        Assert.True(File.Exists(PhysicalPath(foreign)));
    }

    // S-5
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://lh3.googleusercontent.com/a/photo")]
    [InlineData("/uploads/listings/x/y.jpg")]
    [InlineData("/uploads/avatars/")]
    public async Task S5_External_Empty_And_Foreign_Urls_Are_A_No_Op(string? url)
    {
        var service = Create();
        var kept = await service.SaveAvatarAsync(Jpeg(), UserId);

        Assert.False(await service.DeleteAvatarAsync(UserId, url));

        Assert.True(File.Exists(PhysicalPath(kept)));
    }

    // S-6: traversal and odd characters never reach the file system.
    [Theory]
    [InlineData("../../../secret.jpg")]
    [InlineData("..%2f..%2fsecret.jpg")]
    [InlineData("..\\..\\secret.jpg")]
    [InlineData("subdir/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.jpg")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.jpg?x=1")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.jpg#frag")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.jpg%00")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA.jpg")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.png")]
    [InlineData("short.jpg")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.jpg.exe")]
    public async Task S6_Traversal_And_Malformed_Names_Are_Rejected(string tail)
    {
        var service = Create();
        var secret = Path.Combine(_contentRoot, "wwwroot", "uploads", "secret.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(secret)!);
        await File.WriteAllBytesAsync(secret, new byte[] { 1 });
        var decoy = Path.Combine(UserDir(UserId), "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.jpg");
        Directory.CreateDirectory(UserDir(UserId));
        await File.WriteAllBytesAsync(decoy, new byte[] { 1 });

        Assert.False(await service.DeleteAvatarAsync(UserId, $"/uploads/avatars/{UserId:N}/{tail}"));

        Assert.True(File.Exists(secret));
        Assert.True(File.Exists(decoy)); // the look-alike real file survives every malformed spelling
    }

    [Fact]
    public async Task S6b_A_Well_Formed_Name_That_Does_Not_Exist_Is_False_Not_An_Error()
    {
        Assert.False(await Create().DeleteAvatarAsync(UserId, $"/uploads/avatars/{UserId:N}/{new string('d', 32)}.jpg"));
    }

    // S-7: a sibling directory sharing the prefix is not "inside" the avatar root.
    [Fact]
    public async Task S7_A_Sibling_Directory_With_The_Same_Prefix_Is_Not_Touched()
    {
        var service = Create();
        var sibling = Path.Combine(_contentRoot, "wwwroot", "uploads", "avatars-evil", UserId.ToString("N"));
        Directory.CreateDirectory(sibling);
        var file = Path.Combine(sibling, new string('a', 32) + ".jpg");
        await File.WriteAllBytesAsync(file, new byte[] { 1 });

        Assert.False(await service.DeleteAvatarAsync(UserId, $"/uploads/avatars-evil/{UserId:N}/{new string('a', 32)}.jpg"));

        Assert.True(File.Exists(file));
    }

    // S-8
    [Fact]
    public async Task S8_Wipe_Removes_The_Whole_Directory_And_Is_Idempotent()
    {
        var service = Create();
        await service.SaveAvatarAsync(Jpeg(), UserId);
        await service.SaveAvatarAsync(Jpeg(), UserId);
        var other = await service.SaveAvatarAsync(Jpeg(), OtherId);

        Assert.True(await service.DeleteAvatarDirectoryAsync(UserId));
        Assert.False(Directory.Exists(UserDir(UserId)));
        Assert.True(await service.DeleteAvatarDirectoryAsync(UserId)); // already gone: still success
        Assert.True(await service.DeleteAvatarDirectoryAsync(Guid.NewGuid())); // never existed
        Assert.True(File.Exists(PhysicalPath(other)));
    }

    // S-9: a failing write leaves no partial file behind.
    [Fact]
    public async Task S9_A_Failing_Source_Stream_Leaves_No_Partial_File_And_Propagates()
    {
        var service = Create();

        await Assert.ThrowsAsync<IOException>(() => service.SaveAvatarAsync(new FailingStream(), UserId));

        Assert.Empty(Directory.GetFiles(UserDir(UserId)));
    }

    [Fact]
    public async Task S9b_A_Cancelled_Save_Leaves_No_Partial_File()
    {
        var service = Create();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SaveAvatarAsync(Jpeg(), UserId, cts.Token));

        Assert.Empty(Directory.GetFiles(UserDir(UserId)));
    }

    // S-10: listing for the sweep.
    [Fact]
    public async Task S10_Listing_Returns_Directories_And_Only_Our_Own_Named_Files_With_Their_Urls()
    {
        var service = Create();
        var url = await service.SaveAvatarAsync(Jpeg(), UserId);
        await File.WriteAllBytesAsync(Path.Combine(UserDir(UserId), "stray.txt"), new byte[] { 1 });

        var names = await service.ListAvatarDirectoryNamesAsync();
        var files = await service.ListAvatarFilesAsync(UserId);

        Assert.Equal(new[] { UserId.ToString("N") }, names);
        var file = Assert.Single(files);
        Assert.Equal(url, file.Url);
        Assert.True((DateTime.UtcNow - file.LastWriteUtc).TotalMinutes < 5);
        Assert.Empty(await service.ListAvatarFilesAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task S10b_Listing_An_Absent_Avatar_Root_Is_Empty_Not_An_Error()
    {
        Assert.Empty(await Create("uploads/never-created").ListAvatarDirectoryNamesAsync());
    }

    // The avatar path must stay under wwwroot, like the other two.
    [Fact]
    public void S11_An_Avatar_Path_Outside_Wwwroot_Is_Refused_At_Construction()
    {
        Assert.Throws<InvalidOperationException>(() => Create("../outside"));
    }

    // Log events (plan 3.3). Forced with real Windows file locks.
    [WindowsOnlyFact]
    public async Task S12_A_Locked_Old_File_Is_False_And_Logs_6901_Without_Throwing()
    {
        var service = Create();
        var url = await service.SaveAvatarAsync(Jpeg(), UserId);
        await using var hold = new FileStream(PhysicalPath(url), FileMode.Open, FileAccess.Read, FileShare.None);

        Assert.False(await service.DeleteAvatarAsync(UserId, url));

        var entry = Assert.Single(_logger.Entries, e => e.EventId.Id == 6901);
        Assert.Equal("AvatarOldFileDeleteFailed", entry.EventId.Name);
    }

    [WindowsOnlyFact]
    public async Task S13_A_Locked_File_Makes_The_Wipe_False_And_Logs_6903_Without_Throwing()
    {
        var service = Create();
        var url = await service.SaveAvatarAsync(Jpeg(), UserId);
        await using var hold = new FileStream(PhysicalPath(url), FileMode.Open, FileAccess.Read, FileShare.None);

        Assert.False(await service.DeleteAvatarDirectoryAsync(UserId));

        var entry = Assert.Single(_logger.Entries, e => e.EventId.Id == 6903);
        Assert.Equal("AvatarDirectoryWipeFailed", entry.EventId.Name);
        Assert.Equal(LogLevel.Error, entry.Level);
    }

    [Fact]
    public void S14_The_Event_Ids_Are_The_Fixed_Contract_Operators_Grep_For()
    {
        Assert.Equal(6901, LocalFileStorageService.AvatarOldFileDeleteFailedEvent.Id);
        Assert.Equal(6902, RentalPlatform.Infrastructure.Persistence.AvatarStore.AvatarCommitAmbiguousEvent.Id);
        Assert.Equal(6903, LocalFileStorageService.AvatarDirectoryWipeFailedEvent.Id);
        Assert.Equal(6904, AvatarOrphanSweepService.AvatarSweepSummaryEvent.Id);
    }

    private sealed class FailingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("boom");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => throw new IOException("boom");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
