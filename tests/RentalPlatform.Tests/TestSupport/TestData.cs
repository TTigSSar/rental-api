using RentalPlatform.Application.Common;
using RentalPlatform.Application.DTOs;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Tests.TestSupport;

// Entity + request builders for tests. Every field a configuration marks required
// is populated so the seed never trips a constraint for an unrelated reason.
public static class TestData
{
    public static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    public static User User(
        Guid id,
        string email,
        bool isBlocked = false,
        UserRole role = UserRole.User,
        string? firstName = null,
        string? lastName = null,
        bool isIdConfirmed = false,
        DateTime? createdAt = null) => new()
    {
        Id = id,
        Email = email,
        PasswordHash = "x",
        FirstName = firstName ?? "Test",
        LastName = lastName ?? "User",
        PreferredLanguage = "en",
        CreatedAt = createdAt ?? DateTime.UtcNow,
        IsBlocked = isBlocked,
        Role = role,
        IsIdConfirmed = isIdConfirmed
    };

    public static Category Category(
        Guid id,
        string? name = null,
        string? slug = null,
        int displayOrder = 0,
        bool isVisible = true,
        string? colorHex = null,
        string? iconName = null) => new()
    {
        Id = id,
        Name = name ?? "Building Blocks",
        Slug = slug ?? $"building-blocks-{id:N}",
        DisplayOrder = displayOrder,
        IsVisible = isVisible,
        ColorHex = colorHex,
        IconName = iconName
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
        string? note = null) => new()
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
        Note = note
    };

    public static Conversation Conversation(
        Guid id,
        Guid bookingId,
        Guid ownerId,
        Guid renterId,
        Guid? lastMessageId = null,
        DateTime? lastMessageAt = null) => new()
    {
        Id = id,
        BookingId = bookingId,
        OwnerId = ownerId,
        RenterId = renterId,
        ToyTitle = "LEGO Duplo Starter Set",
        LastMessageId = lastMessageId,
        LastMessageAt = lastMessageAt,
        CreatedAt = DateTime.UtcNow
    };

    public static ChatMessage ChatMessage(
        Guid id,
        Guid conversationId,
        Guid? senderId,
        string? body = "Hello",
        MessageType type = MessageType.Text) => new()
    {
        Id = id,
        ConversationId = conversationId,
        SenderId = senderId,
        Type = type,
        Body = body,
        CreatedAt = DateTime.UtcNow
    };

    // Severity is always derived from ReasonCode via ReportReasonCatalog — same as production
    // (ReportsService.CreateAsync) — never overridable here, so tests can't accidentally seed a
    // reason/severity combination production could never produce.
    public static Report Report(
        Guid id,
        ReportTargetType targetType,
        Guid targetId,
        Guid reporterUserId,
        string reasonCode = "other",
        ReportStatus status = ReportStatus.Open,
        string? targetLabel = null,
        string? detail = null,
        DateTime? createdAt = null,
        DateTime? resolvedAt = null,
        Guid? resolvedByUserId = null,
        string? resolutionNote = null) => new()
    {
        Id = id,
        TargetType = targetType,
        TargetId = targetId,
        TargetLabel = targetLabel ?? "Test target",
        ReporterUserId = reporterUserId,
        ReasonCode = reasonCode,
        Detail = detail,
        Severity = ReportReasonCatalog.SeverityFor(reasonCode),
        Status = status,
        CreatedAt = createdAt ?? DateTime.UtcNow,
        ResolvedAt = resolvedAt,
        ResolvedByUserId = resolvedByUserId,
        ResolutionNote = resolutionNote
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
