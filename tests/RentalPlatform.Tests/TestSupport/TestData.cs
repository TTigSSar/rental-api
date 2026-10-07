using RentalPlatform.Application.Common;
using RentalPlatform.Application.DTOs;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Infrastructure.Services;

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
        DateTime? createdAt = null,
        string? passwordHash = null,
        // Home point (home-point model). Deliberately NULL by default — an owner without one cannot
        // create a listing, and defaulting these would quietly hide that gate from every test that
        // did not think about it. A test that needs a listable owner says so, e.g. with Yerevan().
        decimal? homeLatitude = null,
        decimal? homeLongitude = null,
        decimal? homePublicLatitude = null,
        decimal? homePublicLongitude = null,
        Guid? homeDistrictId = null,
        string? preferredLanguage = "en") => new()
    {
        Id = id,
        Email = email,
        PasswordHash = passwordHash ?? "x",
        FirstName = firstName ?? "Test",
        LastName = lastName ?? "User",
        PreferredLanguage = preferredLanguage,
        CreatedAt = createdAt ?? DateTime.UtcNow,
        IsBlocked = isBlocked,
        Role = role,
        IsIdConfirmed = isIdConfirmed,
        HomeLatitude = homeLatitude,
        HomeLongitude = homeLongitude,
        HomePublicLatitude = homePublicLatitude,
        HomePublicLongitude = homePublicLongitude,
        HomeDistrictId = homeDistrictId,
        HomePointUpdatedAt = homeLatitude is null ? null : DateTime.UtcNow
    };

    /// <summary>
    /// A point inside Kentron, Yerevan — the default "this owner can list toys" home point. Verified
    /// against DistrictBoundaryProvider.FindDistrictCode, same convention as the dev seed.
    /// </summary>
    public static readonly (decimal Latitude, decimal Longitude) KentronPoint = (40.1856m, 44.5126m);

    /// <summary>A point in Arabkir — used wherever a test needs a SECOND, different Yerevan district.</summary>
    public static readonly (decimal Latitude, decimal Longitude) ArabkirPoint = (40.2010m, 44.5090m);

    /// <summary>
    /// Gyumri — a real Armenian city, and outside every Yerevan district. The canonical "this point
    /// must be refused" fixture: DoRent operates in Yerevan only, so being in the right country buys
    /// a home point nothing (see IHomePointService.ValidateForSave).
    /// </summary>
    public static readonly (decimal Latitude, decimal Longitude) OutsideYerevanPoint = (40.7894m, 43.8475m);

    // Fixed district Guids from DistrictConfiguration.HasData (seeded by EnsureCreated), so a test
    // can assert on a district without querying for it first.
    public static readonly Guid KentronDistrictId = new("d0000007-0000-4000-9000-000000000007");
    public static readonly Guid ArabkirDistrictId = new("d0000002-0000-4000-9000-000000000002");

    /// <summary>
    /// A user who already has a home point, i.e. one allowed to create listings.
    /// </summary>
    /// <remarks>
    /// The derived half of the home point (the snapped public pair and the district) is filled in
    /// the way the real service would: the public pair through the real GeohashSnapper, the district
    /// from the point's known answer. This matters because those derived values are what the rest of
    /// the system reads — an owner with an exact point but no district behaves like an owner living
    /// outside Yerevan, which is a different test.
    /// </remarks>
    public static User OwnerWithHome(
        Guid id,
        string email,
        (decimal Latitude, decimal Longitude)? point = null,
        Guid? homeDistrictId = null,
        bool isBlocked = false,
        string? preferredLanguage = "en")
    {
        var resolved = point ?? KentronPoint;
        var (latitude, longitude) = resolved;
        var (publicLatitude, publicLongitude) = new GeohashSnapper().SnapToCellCenter(latitude, longitude);

        return User(
            id,
            email,
            isBlocked: isBlocked,
            homeLatitude: latitude,
            homeLongitude: longitude,
            homePublicLatitude: publicLatitude,
            homePublicLongitude: publicLongitude,
            homeDistrictId: homeDistrictId ?? DistrictIdFor(resolved),
            preferredLanguage: preferredLanguage);
    }

    // Only the points this file declares are known here; anything else is treated as
    // "outside Yerevan" and the caller passes homeDistrictId explicitly if that is wrong.
    private static Guid? DistrictIdFor((decimal Latitude, decimal Longitude) point)
    {
        if (point == KentronPoint) return KentronDistrictId;
        if (point == ArabkirPoint) return ArabkirDistrictId;
        return null;
    }

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
        Currency = "AMD",
        Country = "Armenia",
        City = "Yerevan",
        CompensationAmount = 25m,
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
