namespace RentalPlatform.Domain.ValueObjects;

// Domain-level modelling tool for "where a listing is" as a single cohesive value, distinct from
// the DB Districts reference table added alongside this type (P1-2) — District here is a plain
// display string, not a foreign key. City mirrors Listing.City (required); District and
// AddressLine mirror the current flat columns' optionality. Immutable, structural equality via
// `sealed record`.
public sealed record Address
{
    public string City { get; }
    public string? District { get; }
    public string? AddressLine { get; }

    public Address(string city, string? district, string? addressLine)
    {
        if (string.IsNullOrWhiteSpace(city))
        {
            throw new ArgumentException("City is required.", nameof(city));
        }

        City = city;
        District = district;
        AddressLine = addressLine;
    }
}
