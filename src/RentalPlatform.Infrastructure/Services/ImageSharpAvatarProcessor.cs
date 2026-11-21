using RentalPlatform.Application.Abstractions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Memory;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace RentalPlatform.Infrastructure.Services;

// ADR-070 point 2 (and its spike amendment). The server is the security boundary: size and magic bytes,
// Identify before decoding, one restricted Configuration (JPEG/PNG/WebP only, allocator limits,
// MaxDegreeOfParallelism 1), one frame, no metadata, then a fresh 512x512 JPEG q85 on white. At most two
// decodes at once; a third waits up to 10 s and is then told to retry (503).
//
// Exceptions are classified, never swallowed wholesale: bad content -> InvalidType, allocator limit ->
// TooLarge, client abort -> OperationCanceledException propagates, anything else (IO, a bug) propagates
// and ends as a 500 in GlobalExceptionMiddleware.
public sealed class ImageSharpAvatarProcessor : IAvatarImageProcessor
{
    public const int MaxUploadBytes = 2 * 1024 * 1024;
    public const int OutputSize = 512;
    public const int JpegQuality = 85;

    // JPEG and PNG: 4096 px per side, 16 MP. WebP ignores TargetSize and decodes in full (about 140 MB at
    // the larger cap), so it is capped lower (ADR-070 amendment).
    private const int MaxSideDefault = 4096;
    private const long MaxPixelsDefault = 16_000_000;
    private const int MaxSideWebp = 2048;
    private const long MaxPixelsWebp = 4_000_000;
    private const int DecodeTargetSize = 1024;

    private readonly SemaphoreSlim _slots;
    private readonly TimeSpan _waitTimeout;
    private readonly Configuration _configuration;

    public ImageSharpAvatarProcessor()
        : this(new SemaphoreSlim(2), TimeSpan.FromSeconds(10), singleBufferMb: 64, allocationMb: 96, accumulativeMb: 192)
    {
    }

    // Tests shrink the limits and the wait; production uses the public constructor above.
    internal ImageSharpAvatarProcessor(SemaphoreSlim slots, TimeSpan waitTimeout, int singleBufferMb, int allocationMb, int accumulativeMb)
    {
        _slots = slots;
        _waitTimeout = waitTimeout;
        _configuration = new Configuration(new JpegConfigurationModule(), new PngConfigurationModule(), new WebpConfigurationModule())
        {
            MaxDegreeOfParallelism = 1,
            MemoryAllocator = MemoryAllocator.Create(new MemoryAllocatorOptions
            {
                SingleBufferAllocationLimitMegabytes = singleBufferMb,
                AllocationLimitMegabytes = allocationMb,
                AccumulativeAllocationLimitMegabytes = accumulativeMb
            })
        };
    }

    internal int AvailableSlots => _slots.CurrentCount;

    public async Task<AvatarProcessingResult> ProcessAsync(Stream content, long length, CancellationToken cancellationToken = default)
    {
        if (length > MaxUploadBytes)
        {
            return new AvatarProcessingResult(AvatarProcessingStatus.TooLarge, null);
        }

        // Read at most one byte past the cap so a lying length cannot make us buffer more.
        var buffer = new byte[MaxUploadBytes + 1];
        var read = 0;
        while (read < buffer.Length)
        {
            var chunk = await content.ReadAsync(buffer.AsMemory(read), cancellationToken);
            if (chunk == 0)
            {
                break;
            }

            read += chunk;
        }

        if (read > MaxUploadBytes)
        {
            return new AvatarProcessingResult(AvatarProcessingStatus.TooLarge, null);
        }

        var magic = DetectMagic(buffer.AsSpan(0, read));
        if (magic is null)
        {
            return new AvatarProcessingResult(AvatarProcessingStatus.InvalidType, null);
        }

        // Released only after a successful acquire; a cancelled wait throws before acquiring.
        if (!await _slots.WaitAsync(_waitTimeout, cancellationToken))
        {
            return new AvatarProcessingResult(AvatarProcessingStatus.Busy, null);
        }

        try
        {
            return Process(buffer, read, magic);
        }
        catch (InvalidMemoryOperationException)
        {
            return new AvatarProcessingResult(AvatarProcessingStatus.TooLarge, null);
        }
        catch (InvalidImageContentException exception) when (exception.InnerException is InvalidMemoryOperationException)
        {
            return new AvatarProcessingResult(AvatarProcessingStatus.TooLarge, null);
        }
        catch (Exception exception) when (exception is UnknownImageFormatException or InvalidImageContentException)
        {
            return new AvatarProcessingResult(AvatarProcessingStatus.InvalidType, null);
        }
        finally
        {
            _slots.Release();
        }
    }

    private AvatarProcessingResult Process(byte[] bytes, int count, string magicFormat)
    {
        var identifyOptions = new DecoderOptions { Configuration = _configuration, SkipMetadata = true, MaxFrames = 1 };

        using (var probe = new MemoryStream(bytes, 0, count, writable: false))
        {
            var format = Image.DetectFormat(identifyOptions, probe);
            if (!string.Equals(format.Name, magicFormat, StringComparison.OrdinalIgnoreCase))
            {
                return new AvatarProcessingResult(AvatarProcessingStatus.InvalidType, null);
            }
        }

        var isWebp = magicFormat == "WEBP";
        var maxSide = isWebp ? MaxSideWebp : MaxSideDefault;
        var maxPixels = isWebp ? MaxPixelsWebp : MaxPixelsDefault;

        using (var identifyStream = new MemoryStream(bytes, 0, count, writable: false))
        {
            var info = Image.Identify(identifyOptions, identifyStream);
            if (info.Width < 1 || info.Height < 1)
            {
                return new AvatarProcessingResult(AvatarProcessingStatus.InvalidType, null);
            }

            if (info.Width > maxSide || info.Height > maxSide || (long)info.Width * info.Height > maxPixels)
            {
                return new AvatarProcessingResult(AvatarProcessingStatus.TooLarge, null);
            }
        }

        var loadOptions = new DecoderOptions
        {
            Configuration = _configuration,
            SkipMetadata = true,
            MaxFrames = 1,
            TargetSize = new Size(DecodeTargetSize, DecodeTargetSize),
            SegmentIntegrityHandling = SegmentIntegrityHandling.Strict
        };

        using var loadStream = new MemoryStream(bytes, 0, count, writable: false);
        using var image = Image.Load<Rgba32>(loadOptions, loadStream);

        image.Mutate(context => context
            .Resize(new ResizeOptions { Size = new Size(OutputSize, OutputSize), Mode = ResizeMode.Crop })
            .BackgroundColor(Color.White));

        // Always re-encoded: nothing of the uploaded file reaches the output. No metadata is written.
        using var output = new MemoryStream();
        image.Metadata.ExifProfile = null;
        image.Metadata.IccProfile = null;
        image.Metadata.XmpProfile = null;
        image.Metadata.IptcProfile = null;
        image.SaveAsJpeg(output, new JpegEncoder { Quality = JpegQuality });
        return new AvatarProcessingResult(AvatarProcessingStatus.Ok, output.ToArray());
    }

    // The format named by the first bytes, or null when it is none of the three we accept.
    private static string? DetectMagic(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
        {
            return "JPEG";
        }

        ReadOnlySpan<byte> png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (data.Length >= png.Length && data[..png.Length].SequenceEqual(png))
        {
            return "PNG";
        }

        if (data.Length >= 12
            && data[0] == (byte)'R' && data[1] == (byte)'I' && data[2] == (byte)'F' && data[3] == (byte)'F'
            && data[8] == (byte)'W' && data[9] == (byte)'E' && data[10] == (byte)'B' && data[11] == (byte)'P')
        {
            return "WEBP";
        }

        return null;
    }
}
