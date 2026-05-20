using System.Diagnostics.CodeAnalysis;

namespace RentalPlatform.Application.Common;

// Inspects the leading bytes of an uploaded file and tells the caller whether
// the byte pattern matches one of the whitelisted image formats. Used as a
// second layer of validation behind extension/content-type checks — those
// trust user-supplied metadata, this one looks at the actual bytes.
public static class ImageContentValidator
{
    // 12 bytes covers WebP's "RIFF....WEBP" marker; the other formats need fewer.
    public const int HeaderBytesRequired = 12;

    public static bool TryDetectMimeType(ReadOnlySpan<byte> header, [NotNullWhen(true)] out string? mimeType)
    {
        // JPEG: FF D8 FF
        if (header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
        {
            mimeType = "image/jpeg";
            return true;
        }

        // PNG: 89 50 4E 47 0D 0A 1A 0A
        if (header.Length >= 8
            && header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47
            && header[4] == 0x0D && header[5] == 0x0A && header[6] == 0x1A && header[7] == 0x0A)
        {
            mimeType = "image/png";
            return true;
        }

        // GIF: 47 49 46 38 ("GIF8") — both 87a and 89a variants begin with this.
        if (header.Length >= 4 && header[0] == 0x47 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x38)
        {
            mimeType = "image/gif";
            return true;
        }

        // WebP: 52 49 46 46 ("RIFF") + 4-byte size + 57 45 42 50 ("WEBP").
        if (header.Length >= 12
            && header[0] == 0x52 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x46
            && header[8] == 0x57 && header[9] == 0x45 && header[10] == 0x42 && header[11] == 0x50)
        {
            mimeType = "image/webp";
            return true;
        }

        mimeType = null;
        return false;
    }
}
