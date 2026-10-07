namespace RentalPlatform.Domain.Entities;

/// <summary>
/// One row per listing, holding the location that listing carried immediately BEFORE the
/// AddUserHomePoint migration collapsed every owner's listings onto their single home point.
/// </summary>
/// <remarks>
/// <para>
/// This is the migration's undo tape, and it is the only one that exists: the collapse is
/// irreversible from the listings themselves, because many distinct points become one. The
/// migration's Down restores every listing from this table, so the table must outlive the Up — it
/// is not a scratch table and must not be cleaned up by a later migration or a maintenance job.
/// </para>
/// <para>
/// It has a second, ongoing reader: the demo-content bootstrap uses the pre-migration coordinates to
/// work out which Yerevan district each showcase listing USED to be in, so the production showcase
/// can be redistributed across districts instead of stacking on one pin. That is why it is modelled
/// as a normal entity rather than created with raw SQL — application code reads it, and the SQLite
/// test databases (EnsureCreated from the model) need it to exist too.
/// </para>
/// <para>
/// Captured once, never updated. A listing created after the migration has no row here, and that is
/// the expected state — absence means "this listing never had a location of its own".
/// </para>
/// </remarks>
public sealed class ListingLocationBeforeHomePoint
{
    /// <summary>The listing this snapshot belongs to. Primary key — one snapshot per listing.</summary>
    public Guid ListingId { get; set; }

    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }
    public Guid? DistrictId { get; set; }

    public string City { get; set; } = string.Empty;
    public string Country { get; set; } = string.Empty;

    /// <summary>When the snapshot was taken, i.e. when the migration ran.</summary>
    public DateTime CapturedAt { get; set; }
}
