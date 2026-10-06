using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RentalPlatform.Domain.Entities;

namespace RentalPlatform.Infrastructure.Configurations;

// Snapshot table written once by the AddUserHomePoint migration — see the entity for why it exists
// and why it must never be cleaned up. Column types mirror Listings exactly, because restoring a
// row means copying these values straight back.
//
// Deliberately NOT a foreign key to Listings: this table's whole job is to survive independently of
// the rows it describes, and a cascade or a restrict from Listings would make deleting a listing
// either destroy its undo record or fail outright.
public sealed class ListingLocationBeforeHomePointConfiguration : IEntityTypeConfiguration<ListingLocationBeforeHomePoint>
{
    public void Configure(EntityTypeBuilder<ListingLocationBeforeHomePoint> builder)
    {
        builder.ToTable("ListingLocationsBeforeHomePoint");

        builder.HasKey(snapshot => snapshot.ListingId);

        builder.Property(snapshot => snapshot.ListingId)
            .ValueGeneratedNever();

        builder.Property(snapshot => snapshot.Latitude)
            .HasPrecision(9, 6);

        builder.Property(snapshot => snapshot.Longitude)
            .HasPrecision(9, 6);

        builder.Property(snapshot => snapshot.City)
            .IsRequired()
            .HasMaxLength(120);

        builder.Property(snapshot => snapshot.Country)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(snapshot => snapshot.CapturedAt)
            .IsRequired();
    }
}
