using RentalPlatform.Application.DTOs;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Tests.TestSupport;

// Entity + request builders for tests. Every field a configuration marks required
// is populated so the seed never trips a constraint for an unrelated reason.
public static class TestData
{
    public static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    public static User User(Guid id, string email, bool isBlocked = false, UserRole role = UserRole.User) => new()
    {
        Id = id,
        Email = email,
        PasswordHash = "x",
        FirstName = "Test",
        LastName = "User",
        PreferredLanguage = "en",
        CreatedAt = DateTime.UtcNow,
        IsBlocked = isBlocked,
        Role = role
    };

    public static Category Category(Guid id) => new()
    {
        Id = id,
        Name = "Building Blocks",
        Slug = $"building-blocks-{id:N}"
    };

    public static Listing Listing(
        Guid id,
        Guid ownerId,
        Guid categoryId,
        ListingStatus status = ListingStatus.Approved) => new()
    {
        Id = id,
        OwnerId = ownerId,
        CategoryId = categoryId,
        Title = "LEGO Duplo Starter Set",
        Description = "A deterministic test listing.",
        PricePerDay = 10m,
        Currency = "USD",
        Country = "Armenia",
        City = "Yerevan",
        DepositAmount = 25m,
        Status = status,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    public static Booking Booking(
        Guid id,
        Guid listingId,
        Guid renterId,
        DateOnly startDate,
        DateOnly endDate,
        BookingStatus status,
        DateTime? expiresAt = null,
        BookingParty? returnInitiatedBy = null,
        DateTime? returnMarkedAt = null) => new()
    {
        Id = id,
        ListingId = listingId,
        RenterId = renterId,
        StartDate = startDate,
        EndDate = endDate,
        TotalPrice = 100m,
        Status = status,
        ExpiresAt = expiresAt ?? DateTime.UtcNow.AddHours(24),
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
        ReturnInitiatedBy = returnInitiatedBy,
        ReturnMarkedAt = returnMarkedAt
    };

    public static ListingImage Image(Guid id, Guid listingId, bool isPrimary, int sortOrder) => new()
    {
        Id = id,
        ListingId = listingId,
        Url = $"/uploads/listings/{listingId:N}/{id:N}.png",
        IsPrimary = isPrimary,
        SortOrder = sortOrder
    };

    // 8-byte PNG signature, then zero-padding to the requested length.
    public static byte[] PngBytes(int length = 64)
    {
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (length < signature.Length)
        {
            length = signature.Length;
        }

        var buffer = new byte[length];
        signature.CopyTo(buffer);
        return buffer;
    }

    // Bytes that match no whitelisted image signature.
    public static byte[] NonImageBytes(int length = 64)
    {
        var buffer = new byte[length];
        Array.Fill(buffer, (byte)0x2A);
        return buffer;
    }

    public static UploadListingImageRequest UploadRequest(
        byte[]? content = null,
        string fileName = "photo.png",
        string contentType = "image/png",
        long? declaredLength = null)
    {
        content ??= PngBytes();
        return new UploadListingImageRequest
        {
            FileName = fileName,
            ContentType = contentType,
            Length = declaredLength ?? content.Length,
            Content = new MemoryStream(content)
        };
    }
}
