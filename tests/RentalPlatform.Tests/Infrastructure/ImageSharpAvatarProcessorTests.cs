using System.Text;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Infrastructure.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace RentalPlatform.Tests.Infrastructure;

// ADR-070 point 2 with the REAL ImageSharp (no mocks): what comes out, what is refused, how exceptions
// are classified, and the decode semaphore.
public sealed class ImageSharpAvatarProcessorTests
{
    private static byte[] Encode(Image image, string format)
    {
        using var stream = new MemoryStream();
        switch (format)
        {
            case "jpeg": image.SaveAsJpeg(stream, new JpegEncoder { Quality = 90 }); break;
            case "png": image.SaveAsPng(stream, new PngEncoder()); break;
            case "webp": image.SaveAsWebp(stream, new WebpEncoder { Quality = 80 }); break;
            default: throw new ArgumentOutOfRangeException(nameof(format));
        }

        return stream.ToArray();
    }

    private static byte[] Make(int width, int height, string format, Rgba32? color = null, Action<Image<Rgba32>>? tweak = null)
    {
        using var image = new Image<Rgba32>(width, height, color ?? new Rgba32(200, 30, 30, 255));
        tweak?.Invoke(image);
        return Encode(image, format);
    }

    private static Task<AvatarProcessingResult> RunAsync(ImageSharpAvatarProcessor processor, byte[] bytes, CancellationToken ct = default) =>
        processor.ProcessAsync(new MemoryStream(bytes), bytes.Length, ct);

    private static ImageSharpAvatarProcessor NewProcessor() => new();

    // ---- output ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("jpeg")]
    [InlineData("png")]
    [InlineData("webp")]
    public async Task BU22_Each_Accepted_Format_Comes_Out_As_A_512_Square_Jpeg(string format)
    {
        var result = await RunAsync(NewProcessor(), Make(800, 600, format));

        Assert.Equal(AvatarProcessingStatus.Ok, result.Status);
        var info = Image.Identify(result.Jpeg!);
        Assert.Equal("JPEG", info.Metadata.DecodedImageFormat!.Name);
        Assert.Equal(512, info.Width);
        Assert.Equal(512, info.Height);
    }

    [Fact]
    public async Task BU23_A_Transparent_Png_Is_Composited_On_White()
    {
        var png = Make(64, 64, "png", new Rgba32(0, 0, 0, 0));

        var result = await RunAsync(NewProcessor(), png);

        Assert.Equal(AvatarProcessingStatus.Ok, result.Status);
        using var decoded = Image.Load<Rgb24>(result.Jpeg!);
        var pixel = decoded[256, 256];
        Assert.True(pixel.R > 245 && pixel.G > 245 && pixel.B > 245, $"expected white, got {pixel}");
    }

    [Fact]
    public async Task BU24_Metadata_And_Appended_Payload_Do_Not_Survive_The_Reencode()
    {
        var jpeg = Make(300, 300, "jpeg", tweak: image =>
        {
            image.Metadata.ExifProfile = new ExifProfile();
            image.Metadata.ExifProfile.SetValue(ExifTag.Software, "SECRET-GPS-MARKER");
            image.Metadata.ExifProfile.SetValue(ExifTag.ImageDescription, "SECRET-GPS-MARKER");
        });
        // A polyglot: a valid JPEG followed by script-looking trailing bytes.
        var polyglot = jpeg.Concat(Encoding.ASCII.GetBytes("<script>alert(1)</script>")).ToArray();
        Assert.Contains("SECRET-GPS-MARKER", Encoding.Latin1.GetString(jpeg));

        var result = await RunAsync(NewProcessor(), polyglot);

        Assert.Equal(AvatarProcessingStatus.Ok, result.Status);
        var text = Encoding.Latin1.GetString(result.Jpeg!);
        Assert.DoesNotContain("SECRET-GPS-MARKER", text);
        Assert.DoesNotContain("<script>", text);
        Assert.Null(Image.Identify(result.Jpeg!).Metadata.ExifProfile);
    }

    [Fact]
    public async Task BU25_A_Non_Square_Image_Is_Centre_Cropped_Not_Stretched()
    {
        // Left third red, middle third green, right third blue; a centre crop of the 3:1 strip is all green.
        var strip = Make(900, 300, "png", tweak: image => image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    row[x] = x < 300 ? new Rgba32(255, 0, 0, 255) : x < 600 ? new Rgba32(0, 255, 0, 255) : new Rgba32(0, 0, 255, 255);
                }
            }
        }));

        var result = await RunAsync(NewProcessor(), strip);

        using var decoded = Image.Load<Rgb24>(result.Jpeg!);
        var left = decoded[10, 256];
        var right = decoded[500, 256];
        Assert.True(left.G > 200 && left.R < 80 && left.B < 80, $"left edge should be green, got {left}");
        Assert.True(right.G > 200 && right.R < 80 && right.B < 80, $"right edge should be green, got {right}");
    }

    // ---- refusals -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("not an image at all")]
    [InlineData("GIF89a......")]
    [InlineData("%PDF-1.4 ....")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'/>")]
    public async Task BU26_Other_Formats_And_Text_Are_InvalidType(string text)
    {
        var result = await RunAsync(NewProcessor(), Encoding.ASCII.GetBytes(text));

        Assert.Equal(AvatarProcessingStatus.InvalidType, result.Status);
        Assert.Null(result.Jpeg);
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4, 5, 6, 7, 8 })]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 9, 9, 9, 9, 9, 9, 9, 9 })]
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 4, 0, 0, 0, 0x57, 0x45, 0x42, 0x50, 1, 2, 3, 4 })]
    public async Task BU27_Magic_Bytes_Without_A_Decodable_Body_Are_InvalidType_Not_A_500(byte[] bytes)
    {
        var result = await RunAsync(NewProcessor(), bytes);

        Assert.Equal(AvatarProcessingStatus.InvalidType, result.Status);
    }

    [Fact]
    public async Task BU28_A_File_Over_2_MiB_Is_TooLarge_Without_Decoding()
    {
        var big = new byte[ImageSharpAvatarProcessor.MaxUploadBytes + 1];
        big[0] = 0xFF; big[1] = 0xD8; big[2] = 0xFF;

        var viaLength = await RunAsync(NewProcessor(), big);
        // A lying length (claims small, delivers big) is caught by the bounded read.
        var lying = await NewProcessor().ProcessAsync(new MemoryStream(big), 10, default);

        Assert.Equal(AvatarProcessingStatus.TooLarge, viaLength.Status);
        Assert.Equal(AvatarProcessingStatus.TooLarge, lying.Status);
    }

    [Fact]
    public async Task BU29_Jpeg_And_Png_Over_4096_Px_Are_TooLarge_Exactly_At_The_Limit_Is_Fine()
    {
        Assert.Equal(AvatarProcessingStatus.TooLarge, (await RunAsync(NewProcessor(), Make(4097, 10, "png"))).Status);
        Assert.Equal(AvatarProcessingStatus.TooLarge, (await RunAsync(NewProcessor(), Make(10, 4097, "jpeg"))).Status);
        // 4096 x 4096 is 16.7 MP: over the 16 MP cap even though each side is within 4096.
        Assert.Equal(AvatarProcessingStatus.TooLarge, (await RunAsync(NewProcessor(), Make(4096, 4096, "png"))).Status);
        Assert.Equal(AvatarProcessingStatus.Ok, (await RunAsync(NewProcessor(), Make(4000, 4000, "jpeg"))).Status); // exactly 16 MP
    }

    [Fact]
    public async Task BU30_WebP_Is_Capped_At_2048_Px_And_4_MP()
    {
        Assert.Equal(AvatarProcessingStatus.TooLarge, (await RunAsync(NewProcessor(), Make(2049, 100, "webp"))).Status);
        Assert.Equal(AvatarProcessingStatus.Ok, (await RunAsync(NewProcessor(), Make(2048, 1900, "webp"))).Status);
        Assert.Equal(AvatarProcessingStatus.TooLarge, (await RunAsync(NewProcessor(), Make(2048, 2048, "webp"))).Status); // 4.19 MP
    }

    [Fact]
    public async Task BU31_An_Allocator_Limit_Failure_Is_TooLarge_Not_InvalidType_Or_500()
    {
        // 1 MB allocation cap, a 1000x1000 RGBA decode needs 4 MB.
        var tiny = new ImageSharpAvatarProcessor(new SemaphoreSlim(2), TimeSpan.FromSeconds(10), singleBufferMb: 1, allocationMb: 1, accumulativeMb: 1);

        var result = await RunAsync(tiny, Make(1000, 1000, "png"));

        Assert.Equal(AvatarProcessingStatus.TooLarge, result.Status);
        Assert.Equal(2, tiny.AvailableSlots); // released on that path
    }

    // ---- semaphore (B-U36) --------------------------------------------------------------------------------

    [Fact]
    public async Task BU36_Cancellation_While_Waiting_Leaves_The_Count_Unchanged()
    {
        var slots = new SemaphoreSlim(1);
        var processor = new ImageSharpAvatarProcessor(slots, TimeSpan.FromSeconds(30), 64, 96, 192);
        await slots.WaitAsync(); // the only slot is taken
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunAsync(processor, Make(50, 50, "png"), cts.Token));

        Assert.Equal(0, slots.CurrentCount); // not released: it was never acquired
        slots.Release();
        Assert.Equal(1, slots.CurrentCount);
    }

    [Fact]
    public async Task BU36b_A_Full_House_Times_Out_As_Busy_And_Does_Not_Release_A_Slot_It_Never_Held()
    {
        var slots = new SemaphoreSlim(1);
        var processor = new ImageSharpAvatarProcessor(slots, TimeSpan.FromMilliseconds(100), 64, 96, 192);
        await slots.WaitAsync();

        var result = await RunAsync(processor, Make(50, 50, "png"));

        Assert.Equal(AvatarProcessingStatus.Busy, result.Status);
        Assert.Equal(0, slots.CurrentCount);
    }

    [Fact]
    public async Task BU36c_Success_And_Classified_Failures_Both_Give_The_Slot_Back()
    {
        var processor = NewProcessor();

        await RunAsync(processor, Make(50, 50, "png"));
        Assert.Equal(2, processor.AvailableSlots);

        await RunAsync(processor, new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4, 5, 6, 7, 8 });
        Assert.Equal(2, processor.AvailableSlots);
    }

    [Fact]
    public async Task BU36d_Two_Concurrent_Decodes_Both_Succeed_And_A_Third_Waits_Its_Turn()
    {
        var processor = NewProcessor();
        var bytes = Make(1200, 1200, "png");

        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Task.Run(() => RunAsync(processor, bytes))));

        Assert.All(results, result => Assert.Equal(AvatarProcessingStatus.Ok, result.Status));
        Assert.Equal(2, processor.AvailableSlots);
    }

    [Fact]
    public async Task BU36e_An_Aborted_Request_Stream_Read_Propagates_As_Cancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            NewProcessor().ProcessAsync(new MemoryStream(Make(50, 50, "png")), 100, cts.Token));
    }
}
