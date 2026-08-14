using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.Common;
using RentalPlatform.Application.DTOs;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Application.Services;

/// <summary>
/// Admin console Phase 2: the Categories screen. Follows the same shape as AdminListingsService —
/// EnsureAdminAsync re-checks the role the controller's [Authorize] already gates (defence in
/// depth), every mutation writes a ModerationLogEntry, errors are ServiceResult/ServiceError.
/// </summary>
public sealed class AdminCategoriesService : IAdminCategoriesService
{
    private static class ErrorCodes
    {
        public const string Unauthenticated = "admin.unauthenticated";
        public const string Forbidden = "admin.forbidden";
        // Reserved for a ROUTE id that doesn't resolve (404) — see FromError in the controller.
        public const string CategoryNotFound = "admin.category_not_found";
        public const string NameTaken = "admin.category_name_taken";
        public const string InvalidName = "admin.category_invalid_name";
        public const string InvalidColor = "admin.category_invalid_color";
        public const string OrderMismatch = "admin.category_order_mismatch";
        public const string ReassignRequired = "admin.category_reassign_required";
        public const string ReassignInvalid = "admin.category_reassign_invalid";
        // Distinct from CategoryNotFound: the reassignToCategoryId QUERY PARAMETER not resolving is
        // a bad request payload, not a missing route resource — 400, not 404.
        public const string ReassignTargetNotFound = "admin.category_reassign_target_not_found";
    }

    // #RRGGBB or #RRGGBBAA.
    private static readonly Regex ColorHexPattern = new(
        "^#[0-9A-Fa-f]{6}([0-9A-Fa-f]{2})?$", RegexOptions.Compiled);

    // ModerationLogEntry.DetailJson is HasMaxLength(2000). Category.Name/Slug are already bounded
    // (120/140 chars — CategoryConfiguration) by the time they reach here since the category save
    // above must have already succeeded, but Truncate + the relaxed encoder are applied here too
    // for the same defence-in-depth reason AdminReportsService/AdminListingsService apply it to
    // their note fields — see MUST-FIX 1 in the 2026-08 code review.
    private static readonly JsonSerializerOptions DetailJsonOptions =
        new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly ICurrentUserContext _currentUserContext;
    private readonly IAdminCategoriesStore _store;
    private readonly IModerationLogStore _moderationLogStore;

    public AdminCategoriesService(
        ICurrentUserContext currentUserContext,
        IAdminCategoriesStore store,
        IModerationLogStore moderationLogStore)
    {
        _currentUserContext = currentUserContext;
        _store = store;
        _moderationLogStore = moderationLogStore;
    }

    public async Task<ServiceResult<AdminCategoriesResponse>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var adminResult = await EnsureAdminAsync(cancellationToken);
        if (!adminResult.IsSuccess)
        {
            return ServiceResult<AdminCategoriesResponse>.Failure(adminResult.Error!);
        }

        var categories = await _store.GetAllOrderedAsync(cancellationToken);
        var counts = await _store.GetApprovedListingCountsAsync(cancellationToken);

        var items = categories.Select(category => MapToResponse(category, counts)).ToList();

        var summary = new AdminCategoriesSummary
        {
            TotalCategories = items.Count,
            VisibleCount = items.Count(i => i.IsVisible),
            TotalListedToys = items.Sum(i => i.ListingCount)
        };

        return ServiceResult<AdminCategoriesResponse>.Success(new AdminCategoriesResponse
        {
            Items = items,
            Summary = summary
        });
    }

    public async Task<ServiceResult<AdminCategoryResponse>> CreateAsync(
        CreateCategoryRequest request, CancellationToken cancellationToken = default)
    {
        var adminResult = await EnsureAdminAsync(cancellationToken);
        if (!adminResult.IsSuccess)
        {
            return ServiceResult<AdminCategoryResponse>.Failure(adminResult.Error!);
        }

        var admin = adminResult.Value!;

        var name = request.Name.Trim();
        if (name.Length == 0)
        {
            return Failure<AdminCategoryResponse>(ErrorCodes.InvalidName, "Category name is required.");
        }

        var colorHex = NormalizeColorOrNull(request.ColorHex);
        if (ReferenceEquals(colorHex, InvalidColor))
        {
            return Failure<AdminCategoryResponse>(ErrorCodes.InvalidColor, "Colour must be #RRGGBB or #RRGGBBAA.");
        }

        var slug = GenerateSlug(name);

        if (await _store.NameOrSlugExistsAsync(name, slug, excludeCategoryId: null, cancellationToken))
        {
            return Failure<AdminCategoryResponse>(ErrorCodes.NameTaken, "A category with this name already exists.");
        }

        var maxOrder = await _store.GetMaxDisplayOrderAsync(cancellationToken);
        var iconName = string.IsNullOrWhiteSpace(request.IconName) ? null : request.IconName.Trim();

        var category = new Category
        {
            Id = Guid.NewGuid(),
            Name = name,
            Slug = slug,
            IconName = iconName,
            ColorHex = colorHex?.Value,
            DisplayOrder = maxOrder + 1,
            IsVisible = true
        };

        await _store.AddAsync(category, cancellationToken);
        await _store.SaveChangesAsync(cancellationToken);

        await _moderationLogStore.AppendAsync(new ModerationLogEntry
        {
            Id = Guid.NewGuid(),
            ActorUserId = admin.Id,
            Action = ModerationAction.CategoryCreated,
            TargetType = ModerationTargetType.Category,
            TargetId = category.Id,
            TargetLabel = Truncate(category.Name),
            DetailJson = JsonSerializer.Serialize(
                new { name = Truncate(category.Name), slug = Truncate(category.Slug) },
                DetailJsonOptions),
            CreatedAt = DateTime.UtcNow
        }, cancellationToken);

        return ServiceResult<AdminCategoryResponse>.Success(MapToResponse(category, listingCount: 0));
    }

    public async Task<ServiceResult<AdminCategoryResponse>> UpdateAsync(
        Guid categoryId, UpdateCategoryRequest request, CancellationToken cancellationToken = default)
    {
        var adminResult = await EnsureAdminAsync(cancellationToken);
        if (!adminResult.IsSuccess)
        {
            return ServiceResult<AdminCategoryResponse>.Failure(adminResult.Error!);
        }

        var admin = adminResult.Value!;

        var category = await _store.FindByIdAsync(categoryId, cancellationToken);
        if (category is null)
        {
            return Failure<AdminCategoryResponse>(ErrorCodes.CategoryNotFound, "Category was not found.");
        }

        var previousName = category.Name;
        var renamed = false;

        // Name is the only field that can invalidate the duplicate-name guard; null means "leave
        // unchanged" (PATCH semantics — omitted field, not "clear the name", which isn't a valid
        // state for a required column anyway).
        if (request.Name is not null)
        {
            var name = request.Name.Trim();
            if (name.Length == 0)
            {
                return Failure<AdminCategoryResponse>(ErrorCodes.InvalidName, "Category name is required.");
            }

            if (!string.Equals(name, category.Name, StringComparison.Ordinal))
            {
                // Slug intentionally NOT regenerated on rename — see AdminCategoriesController for
                // why (home-page category sections and the admin/renter frontends both resolve
                // categories by slug; a slug rewrite would silently break those lookups).
                if (await _store.NameOrSlugExistsAsync(name, category.Slug, categoryId, cancellationToken))
                {
                    return Failure<AdminCategoryResponse>(ErrorCodes.NameTaken, "A category with this name already exists.");
                }

                category.Name = name;
                renamed = true;
            }
        }

        // IconName/ColorHex: null means "leave unchanged"; an empty/whitespace string means
        // "clear this field" (there is no other way to null out an optional PATCH field in JSON
        // without a full wrapper type, and both fields are purely cosmetic so this is low-risk).
        if (request.IconName is not null)
        {
            var trimmed = request.IconName.Trim();
            category.IconName = trimmed.Length == 0 ? null : trimmed;
        }

        if (request.ColorHex is not null)
        {
            var normalized = NormalizeColorOrNull(request.ColorHex);
            if (ReferenceEquals(normalized, InvalidColor))
            {
                return Failure<AdminCategoryResponse>(ErrorCodes.InvalidColor, "Colour must be #RRGGBB or #RRGGBBAA.");
            }

            category.ColorHex = normalized?.Value;
        }

        await _store.SaveChangesAsync(cancellationToken);

        if (renamed)
        {
            await _moderationLogStore.AppendAsync(new ModerationLogEntry
            {
                Id = Guid.NewGuid(),
                ActorUserId = admin.Id,
                Action = ModerationAction.CategoryRenamed,
                TargetType = ModerationTargetType.Category,
                TargetId = category.Id,
                TargetLabel = Truncate(category.Name),
                DetailJson = JsonSerializer.Serialize(
                    new { fromName = Truncate(previousName), toName = Truncate(category.Name) },
                    DetailJsonOptions),
                CreatedAt = DateTime.UtcNow
            }, cancellationToken);
        }

        var counts = await _store.GetApprovedListingCountsAsync(cancellationToken);
        return ServiceResult<AdminCategoryResponse>.Success(MapToResponse(category, counts));
    }

    public async Task<ServiceResult<AdminCategoryResponse>> UpdateVisibilityAsync(
        Guid categoryId, bool isVisible, CancellationToken cancellationToken = default)
    {
        var adminResult = await EnsureAdminAsync(cancellationToken);
        if (!adminResult.IsSuccess)
        {
            return ServiceResult<AdminCategoryResponse>.Failure(adminResult.Error!);
        }

        var admin = adminResult.Value!;

        var category = await _store.FindByIdAsync(categoryId, cancellationToken);
        if (category is null)
        {
            return Failure<AdminCategoryResponse>(ErrorCodes.CategoryNotFound, "Category was not found.");
        }

        if (category.IsVisible == isVisible)
        {
            // No-op: still 200, no log entry (nothing changed) — same convention as
            // AdminListingsService.UpdateCategoryAsync's already-target-category case.
            var unchangedCounts = await _store.GetApprovedListingCountsAsync(cancellationToken);
            return ServiceResult<AdminCategoryResponse>.Success(MapToResponse(category, unchangedCounts));
        }

        category.IsVisible = isVisible;
        await _store.SaveChangesAsync(cancellationToken);

        await _moderationLogStore.AppendAsync(new ModerationLogEntry
        {
            Id = Guid.NewGuid(),
            ActorUserId = admin.Id,
            Action = ModerationAction.CategoryVisibilityChanged,
            TargetType = ModerationTargetType.Category,
            TargetId = category.Id,
            TargetLabel = Truncate(category.Name),
            DetailJson = JsonSerializer.Serialize(new { isVisible }, DetailJsonOptions),
            CreatedAt = DateTime.UtcNow
        }, cancellationToken);

        var counts = await _store.GetApprovedListingCountsAsync(cancellationToken);
        return ServiceResult<AdminCategoryResponse>.Success(MapToResponse(category, counts));
    }

    public async Task<ServiceResult<IReadOnlyCollection<AdminCategoryResponse>>> ReorderAsync(
        IReadOnlyList<Guid> orderedIds, CancellationToken cancellationToken = default)
    {
        var adminResult = await EnsureAdminAsync(cancellationToken);
        if (!adminResult.IsSuccess)
        {
            return ServiceResult<IReadOnlyCollection<AdminCategoryResponse>>.Failure(adminResult.Error!);
        }

        var admin = adminResult.Value!;

        var existing = await _store.GetAllOrderedAsync(cancellationToken);
        var existingIds = existing.Select(c => c.Id).ToHashSet();
        var requestedIds = orderedIds.ToHashSet();

        // A partial reorder would silently corrupt the ordering (categories left out would keep a
        // stale DisplayOrder that now collides/interleaves with the reordered set), so the id set
        // must match exactly — same count, same members, no duplicates.
        if (orderedIds.Count != existing.Count || requestedIds.Count != orderedIds.Count ||
            !existingIds.SetEquals(requestedIds))
        {
            return Failure<IReadOnlyCollection<AdminCategoryResponse>>(
                ErrorCodes.OrderMismatch, "The submitted category order must include every existing category exactly once.");
        }

        await _store.ReorderAsync(orderedIds, cancellationToken);

        await _moderationLogStore.AppendAsync(new ModerationLogEntry
        {
            Id = Guid.NewGuid(),
            ActorUserId = admin.Id,
            Action = ModerationAction.CategoryReordered,
            TargetType = ModerationTargetType.Category,
            TargetId = Guid.Empty,
            TargetLabel = $"{orderedIds.Count} categories",
            // Not the full id list: DetailJson is capped at 2000 chars (ModerationLogEntryConfiguration)
            // and a category catalog can grow past what fits comfortably: the count plus the new
            // order (already reflected in each category's own DisplayOrder) is enough context.
            DetailJson = JsonSerializer.Serialize(new { categoryCount = orderedIds.Count }, DetailJsonOptions),
            CreatedAt = DateTime.UtcNow
        }, cancellationToken);

        var reordered = await _store.GetAllOrderedAsync(cancellationToken);
        var counts = await _store.GetApprovedListingCountsAsync(cancellationToken);
        var items = reordered.Select(category => MapToResponse(category, counts)).ToList();

        return ServiceResult<IReadOnlyCollection<AdminCategoryResponse>>.Success(items);
    }

    public async Task<ServiceResult<bool>> DeleteAsync(
        Guid categoryId, Guid? reassignToCategoryId, CancellationToken cancellationToken = default)
    {
        var adminResult = await EnsureAdminAsync(cancellationToken);
        if (!adminResult.IsSuccess)
        {
            return ServiceResult<bool>.Failure(adminResult.Error!);
        }

        var admin = adminResult.Value!;

        var category = await _store.FindByIdAsync(categoryId, cancellationToken);
        if (category is null)
        {
            return Failure<bool>(ErrorCodes.CategoryNotFound, "Category was not found.");
        }

        var listingCount = await _store.GetListingCountAnyStatusAsync(categoryId, cancellationToken);

        if (listingCount == 0)
        {
            await _store.DeleteAsync(category, cancellationToken);

            await _moderationLogStore.AppendAsync(new ModerationLogEntry
            {
                Id = Guid.NewGuid(),
                ActorUserId = admin.Id,
                Action = ModerationAction.CategoryDeleted,
                TargetType = ModerationTargetType.Category,
                TargetId = category.Id,
                TargetLabel = Truncate(category.Name),
                DetailJson = JsonSerializer.Serialize(new { reassignedListingCount = 0 }, DetailJsonOptions),
                CreatedAt = DateTime.UtcNow
            }, cancellationToken);

            return ServiceResult<bool>.Success(true);
        }

        if (reassignToCategoryId is null)
        {
            return Failure<bool>(
                ErrorCodes.ReassignRequired,
                "This category still has listings; reassignToCategoryId is required to delete it.");
        }

        if (reassignToCategoryId.Value == categoryId)
        {
            return Failure<bool>(ErrorCodes.ReassignInvalid, "Cannot reassign a category's listings to itself.");
        }

        var target = await _store.FindByIdAsync(reassignToCategoryId.Value, cancellationToken);
        if (target is null)
        {
            return Failure<bool>(ErrorCodes.ReassignTargetNotFound, "The reassignment target category was not found.");
        }

        await _store.DeleteWithReassignAsync(categoryId, target.Id, cancellationToken);

        await _moderationLogStore.AppendAsync(new ModerationLogEntry
        {
            Id = Guid.NewGuid(),
            ActorUserId = admin.Id,
            Action = ModerationAction.CategoryDeleted,
            TargetType = ModerationTargetType.Category,
            TargetId = category.Id,
            TargetLabel = Truncate(category.Name),
            DetailJson = JsonSerializer.Serialize(new
            {
                reassignedListingCount = listingCount,
                reassignedToCategoryId = target.Id,
                reassignedToCategoryName = Truncate(target.Name)
            }, DetailJsonOptions),
            CreatedAt = DateTime.UtcNow
        }, cancellationToken);

        return ServiceResult<bool>.Success(true);
    }

    // Returns the authenticated admin User or a failure result. Mirrors
    // AdminListingsService.EnsureAdminAsync exactly (defence in depth alongside the controller's
    // [Authorize(Roles = "Admin")]).
    private async Task<ServiceResult<User>> EnsureAdminAsync(CancellationToken cancellationToken)
    {
        if (_currentUserContext.UserId is not { } userId)
        {
            return Failure<User>(ErrorCodes.Unauthenticated, "Current user is not authenticated.");
        }

        var user = await _store.FindUserByIdAsync(userId, cancellationToken);
        if (user is null)
        {
            return Failure<User>(ErrorCodes.Unauthenticated, "Current user is not authenticated.");
        }

        if (user.Role != UserRole.Admin || user.IsBlocked)
        {
            return Failure<User>(ErrorCodes.Forbidden, "Admin privileges are required.");
        }

        return ServiceResult<User>.Success(user);
    }

    private static AdminCategoryResponse MapToResponse(Category category, IReadOnlyDictionary<Guid, int> counts)
    {
        counts.TryGetValue(category.Id, out var count);
        return MapToResponse(category, count);
    }

    private static AdminCategoryResponse MapToResponse(Category category, int listingCount) => new()
    {
        Id = category.Id,
        Name = category.Name,
        Slug = category.Slug,
        IconName = category.IconName,
        ColorHex = category.ColorHex,
        DisplayOrder = category.DisplayOrder,
        IsVisible = category.IsVisible,
        ListingCount = listingCount
    };

    // Lowercase, hyphenated, alphanumeric-only slug — matches the convention of the fixed dev-seed
    // slugs (see DevelopmentSeedData.Categories). Falls back to a short random token in the
    // pathological case of a name with zero alphanumeric characters (Name is required and
    // non-empty, so this is defensive, not expected in practice).
    private static string GenerateSlug(string name)
    {
        var builder = new StringBuilder();
        var lastWasHyphen = true; // suppress a leading hyphen
        foreach (var ch in name.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
                lastWasHyphen = false;
            }
            else if (!lastWasHyphen)
            {
                builder.Append('-');
                lastWasHyphen = true;
            }
        }

        var slug = builder.ToString().TrimEnd('-');
        if (slug.Length == 0)
        {
            slug = $"category-{Guid.NewGuid():N}"[..24];
        }

        return slug.Length <= 140 ? slug : slug[..140];
    }

    // Sentinel distinguishing "no colour supplied" (null) from "colour supplied but malformed"
    // (the InvalidColor singleton) without a third enum/out-param — NormalizedColor.Value is the
    // trimmed, uppercased-hash-prefixed string ready to store.
    private static readonly NormalizedColor InvalidColor = new(string.Empty);

    private sealed record NormalizedColor(string Value);

    private static NormalizedColor? NormalizeColorOrNull(string? colorHex)
    {
        if (string.IsNullOrWhiteSpace(colorHex))
        {
            return null;
        }

        var trimmed = colorHex.Trim();
        return ColorHexPattern.IsMatch(trimmed) ? new NormalizedColor(trimmed) : InvalidColor;
    }

    private static string Truncate(string value) =>
        value.Length <= 300 ? value : value[..300];

    private static ServiceResult<T> Failure<T>(string code, string message) =>
        ServiceResult<T>.Failure(new ServiceError { Code = code, Message = message });
}
