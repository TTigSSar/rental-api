namespace RentalPlatform.Domain.Entities;

public sealed class Category
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string? IconName { get; set; }
    public string? ImageUrl { get; set; }
    public int DisplayOrder { get; set; }

    // Admin console Phase 2: whether renters can browse this category and its listings. Defaults
    // to true so nothing existing disappears; the migration backfills every pre-existing row to
    // true for the same reason. Listings in a hidden category stay reachable by direct link and
    // in their owner's my-listings — only category-browse/category-filtered public surfaces hide
    // them (see AdminCategoriesController report for the exact scope).
    public bool IsVisible { get; set; } = true;

    // Pastel tile colour shown in the admin categories table and (future) renter UI. "#RRGGBB" or
    // "#RRGGBBAA" — max length 9. Nullable: categories created before this field, or created
    // without an explicit colour, simply render with a default tint client-side.
    public string? ColorHex { get; set; }

    public ICollection<Listing> Listings { get; set; } = new List<Listing>();
}
