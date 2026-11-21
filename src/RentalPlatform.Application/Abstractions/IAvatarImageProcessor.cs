namespace RentalPlatform.Application.Abstractions;

public enum AvatarProcessingStatus
{
    Ok = 0,

    /// <summary>Not a JPEG/PNG/WebP, undecodable, or the detected format differs from the magic bytes.</summary>
    InvalidType = 1,

    /// <summary>Over the size, dimension, pixel or allocator limits.</summary>
    TooLarge = 2,

    /// <summary>No decode slot became free within the wait time.</summary>
    Busy = 3
}

public sealed record AvatarProcessingResult(AvatarProcessingStatus Status, byte[]? Jpeg);

// ADR-070 point 2: the server is the security boundary. Implementations decode through a restricted
// configuration and always re-encode (512x512 JPEG, no metadata); the uploaded bytes are never stored.
// A client abort propagates as OperationCanceledException; unexpected failures are NOT caught here.
public interface IAvatarImageProcessor
{
    Task<AvatarProcessingResult> ProcessAsync(Stream content, long length, CancellationToken cancellationToken = default);
}
