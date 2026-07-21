namespace RentalPlatform.Domain.Entities;

// Reference data: the 12 Yerevan administrative districts, mirroring the `code`/`nameEn`/
// `nameHy`/`nameRu` properties of the yerevan-districts.geojson asset (Infrastructure/Resources)
// byte-for-byte. Populating Listing.DistrictId from a point-in-polygon lookup against that same
// asset is a later card (P1-4) — this table only exists to be pointed at for now.
public sealed class District
{
    public Guid Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string NameEn { get; set; } = string.Empty;
    public string NameHy { get; set; } = string.Empty;
    public string NameRu { get; set; } = string.Empty;

    public ICollection<Listing> Listings { get; set; } = new List<Listing>();
}
