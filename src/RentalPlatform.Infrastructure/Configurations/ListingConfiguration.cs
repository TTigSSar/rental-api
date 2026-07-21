using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RentalPlatform.Domain.Entities;

namespace RentalPlatform.Infrastructure.Configurations;

public sealed class ListingConfiguration : IEntityTypeConfiguration<Listing>
{
    public void Configure(EntityTypeBuilder<Listing> builder)
    {
        builder.ToTable("Listings");

        builder.HasKey(listing => listing.Id);

        builder.Property(listing => listing.Title)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(listing => listing.Description)
            .IsRequired()
            .HasMaxLength(4000);

        builder.Property(listing => listing.PricePerDay)
            .HasPrecision(18, 2)
            .IsRequired();

        // Persisted as int like the project's other enums (ListingStatus, BookingStatus).
        // DB default Daily (1) backfills existing rows when the column is added. The sentinel is
        // Daily (matching the entity initializer) so the store default is only substituted for an
        // unset/Daily value — an explicitly chosen Hourly (CLR default 0) is still persisted.
        builder.Property(listing => listing.PriceUnit)
            .IsRequired()
            .HasDefaultValue(Domain.Enums.PriceUnit.Daily)
            .HasSentinel(Domain.Enums.PriceUnit.Daily);

        builder.Property(listing => listing.Currency)
            .IsRequired()
            .HasMaxLength(3);

        builder.Property(listing => listing.Country)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(listing => listing.City)
            .IsRequired()
            .HasMaxLength(120);

        builder.Property(listing => listing.AddressLine)
            .HasMaxLength(250);

        builder.Property(listing => listing.Latitude)
            .HasPrecision(9, 6);

        builder.Property(listing => listing.Longitude)
            .HasPrecision(9, 6);

        // Privacy-model additions (P1-2 schema only — see Listing.cs; nothing computes these yet,
        // that's P1-4). Same precision/scale as Latitude/Longitude since both hold the same kind
        // of WGS84 value.
        builder.Property(listing => listing.PublicLatitude)
            .HasPrecision(9, 6);

        builder.Property(listing => listing.PublicLongitude)
            .HasPrecision(9, 6);

        // Persisted as int like the project's other enums (ListingStatus, PriceUnit). Unlike
        // PriceUnit's Daily/Hourly, Home (0) is both the CLR default and the desired DB default,
        // so a plain HasDefaultValue is enough — no sentinel needed to disambiguate "unset" from
        // an explicitly chosen zero value.
        builder.Property(listing => listing.LocationKind)
            .IsRequired()
            .HasDefaultValue(Domain.Enums.LocationKind.Home);

        builder.Property(listing => listing.Status)
            .IsRequired();

        builder.Property(listing => listing.CreatedAt)
            .IsRequired();

        builder.Property(listing => listing.UpdatedAt)
            .IsRequired();

        // Moderation fields — all nullable; populated only when an admin acts on the listing.
        builder.Property(listing => listing.RejectionReason)
            .HasMaxLength(1000);

        builder.Property(listing => listing.RejectionReasonCode)
            .HasMaxLength(64);

        builder.Property(listing => listing.RejectionNote)
            .HasMaxLength(1000);

        builder.Property(listing => listing.ModeratedAt);

        builder.Property(listing => listing.ModeratedByUserId);

        // Toy-rental MVP optional fields. Nullable in DB so generic-listing rows stay valid.
        builder.Property(listing => listing.AgeFromMonths);

        builder.Property(listing => listing.AgeToMonths);

        builder.Property(listing => listing.Condition)
            .HasMaxLength(50);

        builder.Property(listing => listing.HygieneNotes)
            .HasMaxLength(1000);

        builder.Property(listing => listing.SafetyNotes)
            .HasMaxLength(1000);

        builder.Property(listing => listing.DepositAmount)
            .HasPrecision(18, 2);

        builder.Property(listing => listing.MinRentalDays);

        // Nullable, persisted as int like the project's other enums (ListingStatus, PriceUnit).
        // No default/sentinel: unlike PriceUnit, an unset delivery method is a real "not specified"
        // state, not a value with an implied fallback.
        builder.Property(listing => listing.DeliveryType);

        builder.HasOne(listing => listing.Owner)
            .WithMany(user => user.Listings)
            .HasForeignKey(listing => listing.OwnerId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(listing => listing.Category)
            .WithMany(category => category.Listings)
            .HasForeignKey(listing => listing.CategoryId)
            .OnDelete(DeleteBehavior.Restrict);

        // Nullable FK — a listing may have no district yet (P1-4 backfills it via point-in-
        // polygon lookup). Restrict: a district with listings against it cannot be deleted out
        // from under them (Districts is fixed reference data anyway, so this should never fire
        // in practice, but Restrict is the safe/explicit choice over a cascading delete here).
        builder.HasOne(listing => listing.District)
            .WithMany(district => district.Listings)
            .HasForeignKey(listing => listing.DistrictId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(listing => listing.Images)
            .WithOne(image => image.Listing)
            .HasForeignKey(image => image.ListingId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(listing => listing.Status);
        builder.HasIndex(listing => listing.City);
        builder.HasIndex(listing => listing.CategoryId);
        builder.HasIndex(listing => listing.DistrictId);

        // Composite index for the Phase 2 bounding-box queries against the public (fuzzed)
        // coordinate — not the exact Latitude/Longitude, which stay owner/admin-only.
        builder.HasIndex(listing => new { listing.PublicLatitude, listing.PublicLongitude });
    }
}
