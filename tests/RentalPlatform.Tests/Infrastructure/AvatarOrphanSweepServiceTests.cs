using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Infrastructure.Services;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Infrastructure;

// ADR-070 point 12 (SW-*): the eventual erasure guarantee. REAL storage on a temp directory and a real
// (SQLite) users table; the clock is the system clock and file ages are set with SetLastWriteTimeUtc.
public sealed class AvatarOrphanSweepServiceTests : IDisposable
{
    private static readonly Guid Live = new("d1000000-0000-0000-0000-000000000001");
    private static readonly Guid Gone = new("d1000000-0000-0000-0000-000000000002");
    private static readonly Guid Missing = new("d1000000-0000-0000-0000-000000000003");
    private static readonly Guid Third = new("d1000000-0000-0000-0000-000000000004");

    private readonly string _contentRoot;
    private readonly SqliteTestDatabase _db = new();
    private readonly ListLogger<AvatarOrphanSweepService> _logger = new();

    public AvatarOrphanSweepServiceTests()
    {
        _contentRoot = Path.Combine(Path.GetTempPath(), "rental-sweep-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_contentRoot, "wwwroot"));
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_contentRoot))
        {
            Directory.Delete(_contentRoot, recursive: true);
        }
    }

    private IFileStorageService RealStorage() =>
        new LocalFileStorageService(
            new FakeHostEnvironment("Test") { ContentRootPath = _contentRoot },
            Options.Create(new LocalFileStorageOptions()));

    private AvatarOrphanSweepService NewSweep(IFileStorageService storage)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => _db.CreateContext());
        services.AddSingleton(storage);
        return new AvatarOrphanSweepService(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System,
            _logger);
    }

    private string AvatarsRoot => Path.Combine(_contentRoot, "wwwroot", "uploads", "avatars");

    private string DirOf(Guid id) => Path.Combine(AvatarsRoot, id.ToString("N"));

    private string WriteFile(Guid id, TimeSpan age, string? name = null)
    {
        Directory.CreateDirectory(DirOf(id));
        var fileName = (name ?? Guid.NewGuid().ToString("N")) + ".jpg";
        var path = Path.Combine(DirOf(id), fileName);
        File.WriteAllBytes(path, new byte[] { 1 });
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - age);
        return $"/uploads/avatars/{id:N}/{fileName}";
    }

    private async Task SeedUsersAsync(string? liveAvatar = null)
    {
        var live = TestData.User(Live, "live@test.local");
        live.AvatarUrl = liveAvatar;
        var gone = TestData.User(Gone, "gone@test.local");
        gone.DeletedAt = DateTime.UtcNow;
        var third = TestData.User(Third, "third@test.local");
        await _db.SeedAsync(live, gone, third);
    }

    // SW-1
    [Fact]
    public async Task SW1_The_Directory_Of_A_Deleted_User_Is_Wiped_Whatever_The_File_Age()
    {
        await SeedUsersAsync();
        WriteFile(Gone, TimeSpan.FromSeconds(5));
        WriteFile(Gone, TimeSpan.FromDays(3));

        var summary = await NewSweep(RealStorage()).SweepAsync();

        Assert.False(Directory.Exists(DirOf(Gone)));
        Assert.Equal(1, summary.DirectoriesWiped);
    }

    // SW-2
    [Fact]
    public async Task SW2_The_Directory_Of_A_User_With_No_Row_Is_Wiped()
    {
        await SeedUsersAsync();
        WriteFile(Missing, TimeSpan.FromDays(1));

        await NewSweep(RealStorage()).SweepAsync();

        Assert.False(Directory.Exists(DirOf(Missing)));
    }

    // SW-3
    [Fact]
    public async Task SW3_An_Old_Orphan_Beside_The_Current_File_Is_Deleted_And_The_Current_File_Is_Kept()
    {
        var current = WriteFile(Live, TimeSpan.FromDays(2));
        var orphan = WriteFile(Live, TimeSpan.FromHours(3));
        await SeedUsersAsync(liveAvatar: current);

        var summary = await NewSweep(RealStorage()).SweepAsync();

        Assert.True(File.Exists(Path.Combine(_contentRoot, "wwwroot", current.TrimStart('/'))));
        Assert.False(File.Exists(Path.Combine(_contentRoot, "wwwroot", orphan.TrimStart('/'))));
        Assert.Equal(1, summary.OrphansDeleted);
    }

    [Fact]
    public async Task SW3b_A_User_Without_A_Current_Avatar_Loses_All_Their_Old_Files_But_Keeps_The_Directory_Row()
    {
        await SeedUsersAsync(liveAvatar: null);
        WriteFile(Live, TimeSpan.FromDays(2));

        var summary = await NewSweep(RealStorage()).SweepAsync();

        Assert.Empty(Directory.GetFiles(DirOf(Live)));
        Assert.Equal(1, summary.OrphansDeleted);
        Assert.Equal(0, summary.DirectoriesWiped);
    }

    // SW-4
    [Fact]
    public async Task SW4_An_Orphan_Younger_Than_One_Hour_Is_Kept_It_May_Be_An_Upload_In_Flight()
    {
        var inFlight = WriteFile(Live, TimeSpan.FromMinutes(20));
        await SeedUsersAsync(liveAvatar: null);

        var summary = await NewSweep(RealStorage()).SweepAsync();

        Assert.True(File.Exists(Path.Combine(_contentRoot, "wwwroot", inFlight.TrimStart('/'))));
        Assert.Equal(0, summary.OrphansDeleted);
    }

    // SW-5
    [Fact]
    public async Task SW5_A_Directory_Whose_Name_Is_Not_A_User_Id_Is_Skipped_And_Logged()
    {
        await SeedUsersAsync();
        var stray = Path.Combine(AvatarsRoot, "not-a-guid");
        Directory.CreateDirectory(stray);
        File.WriteAllBytes(Path.Combine(stray, "keep.jpg"), new byte[] { 1 });
        var dashed = Path.Combine(AvatarsRoot, Gone.ToString("D")); // right id, wrong spelling: never trusted
        Directory.CreateDirectory(dashed);

        var summary = await NewSweep(RealStorage()).SweepAsync();

        Assert.True(File.Exists(Path.Combine(stray, "keep.jpg")));
        Assert.True(Directory.Exists(dashed));
        Assert.Equal(2, summary.Skipped);
        Assert.Contains(_logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("not-a-guid"));
    }

    // SW-6
    [Fact]
    public async Task SW6_An_Io_Failure_On_One_Directory_Does_Not_Stop_The_Others_And_Shows_In_The_6904_Summary()
    {
        await SeedUsersAsync();
        WriteFile(Gone, TimeSpan.FromDays(1));
        WriteFile(Missing, TimeSpan.FromDays(1));
        var storage = new FailingWipeStorage(RealStorage(), failFor: Gone);

        var summary = await NewSweep(storage).SweepAsync();

        Assert.True(Directory.Exists(DirOf(Gone))); // the failing one stays for the next run
        Assert.False(Directory.Exists(DirOf(Missing)));
        Assert.Equal(1, summary.Failures);
        Assert.Equal(1, summary.DirectoriesWiped);
        var line = Assert.Single(_logger.Entries, e => e.EventId.Id == 6904);
        Assert.Equal(LogLevel.Information, line.Level);
        Assert.Contains("1 failures", line.Message);
        Assert.Contains("1 wiped", line.Message);
    }

    [Fact]
    public async Task SW6b_A_Throwing_Storage_Call_Is_Counted_As_A_Failure_Not_Thrown_Out_Of_The_Sweep()
    {
        await SeedUsersAsync();
        WriteFile(Gone, TimeSpan.FromDays(1));
        WriteFile(Missing, TimeSpan.FromDays(1));
        var storage = new FailingWipeStorage(RealStorage(), failFor: Gone) { Throw = true };

        var summary = await NewSweep(storage).SweepAsync();

        Assert.Equal(1, summary.Failures);
        Assert.False(Directory.Exists(DirOf(Missing)));
    }

    [Fact]
    public async Task SW7_An_Absent_Avatar_Root_Is_An_Empty_Sweep_With_A_Summary()
    {
        var summary = await NewSweep(RealStorage()).SweepAsync();

        Assert.Equal(new AvatarOrphanSweepService.SweepSummary(0, 0, 0, 0, 0), summary);
        Assert.Single(_logger.Entries, e => e.EventId.Id == 6904);
    }

    [Fact]
    public async Task SW8_More_Directories_Than_One_Batch_Are_All_Processed()
    {
        await SeedUsersAsync();
        var ids = Enumerable.Range(0, 450).Select(_ => Guid.NewGuid()).ToList(); // no rows: all orphans
        foreach (var id in ids)
        {
            Directory.CreateDirectory(DirOf(id));
        }

        var summary = await NewSweep(RealStorage()).SweepAsync();

        Assert.Equal(450, summary.DirectoriesWiped);
        Assert.Empty(Directory.GetDirectories(AvatarsRoot));
    }

    [Fact]
    public async Task SW9_Cancellation_Propagates_And_Leaves_The_Rest_Untouched()
    {
        await SeedUsersAsync();
        WriteFile(Gone, TimeSpan.FromDays(1));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NewSweep(RealStorage()).SweepAsync(cts.Token));
    }

    [Fact]
    public void SW10_The_Schedule_Is_Ten_Minutes_After_Start_Then_Every_24_Hours_With_A_One_Hour_Age_Guard()
    {
        Assert.Equal(TimeSpan.FromMinutes(10), AvatarOrphanSweepService.StartupDelay);
        Assert.Equal(TimeSpan.FromHours(24), AvatarOrphanSweepService.SweepInterval);
        Assert.Equal(TimeSpan.FromHours(1), AvatarOrphanSweepService.OrphanMinAge);
    }

    // Wraps the real storage and makes the wipe of one directory fail (return false, or throw).
    private sealed class FailingWipeStorage : IFileStorageService
    {
        private readonly IFileStorageService _inner;
        private readonly Guid _failFor;

        public FailingWipeStorage(IFileStorageService inner, Guid failFor)
        {
            _inner = inner;
            _failFor = failFor;
        }

        public bool Throw { get; init; }

        public Task<bool> DeleteAvatarDirectoryAsync(Guid userId, CancellationToken cancellationToken = default)
        {
            if (userId != _failFor)
            {
                return _inner.DeleteAvatarDirectoryAsync(userId, cancellationToken);
            }

            return Throw ? throw new IOException("locked") : Task.FromResult(false);
        }

        public Task<string> SaveListingImageAsync(Stream content, string fileName, string contentType, Guid listingId, CancellationToken cancellationToken = default) =>
            _inner.SaveListingImageAsync(content, fileName, contentType, listingId, cancellationToken);
        public Task<bool> DeleteListingImageAsync(string url, CancellationToken cancellationToken = default) => _inner.DeleteListingImageAsync(url, cancellationToken);
        public Task<string> SaveChatAttachmentAsync(Stream content, string fileName, string contentType, Guid conversationId, CancellationToken cancellationToken = default) =>
            _inner.SaveChatAttachmentAsync(content, fileName, contentType, conversationId, cancellationToken);
        public Task<string> SaveAvatarAsync(Stream content, Guid userId, CancellationToken cancellationToken = default) => _inner.SaveAvatarAsync(content, userId, cancellationToken);
        public Task<bool> DeleteAvatarAsync(Guid callerId, string? url, CancellationToken cancellationToken = default) => _inner.DeleteAvatarAsync(callerId, url, cancellationToken);
        public Task<IReadOnlyList<string>> ListAvatarDirectoryNamesAsync(CancellationToken cancellationToken = default) => _inner.ListAvatarDirectoryNamesAsync(cancellationToken);
        public Task<IReadOnlyList<AvatarFileEntry>> ListAvatarFilesAsync(Guid userId, CancellationToken cancellationToken = default) => _inner.ListAvatarFilesAsync(userId, cancellationToken);
    }
}
