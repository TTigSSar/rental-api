namespace RentalPlatform.Domain.Entities;

/// <summary>
/// Admin console Phase 6: one keyword that, when found in a PendingApproval listing's title or
/// description, is a signal the listing belongs in <see cref="CategoryId"/> — the input to the
/// "Needs category fix" suggestion (AdminListingsService). Seeded data only in this phase; there
/// is no admin CRUD for keywords yet.
/// </summary>
public sealed class CategoryKeyword
{
    public Guid Id { get; set; }
    public Guid CategoryId { get; set; }
    public string Keyword { get; set; } = string.Empty;

    public Category Category { get; set; } = null!;
}
