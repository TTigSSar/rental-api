using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RentalPlatform.Domain.Entities;

namespace RentalPlatform.Infrastructure.Configurations;

public sealed class BookingConfiguration : IEntityTypeConfiguration<Booking>
{
    public void Configure(EntityTypeBuilder<Booking> builder)
    {
        builder.ToTable("Bookings");

        builder.HasKey(booking => booking.Id);

        builder.Property(booking => booking.StartDate)
            .HasColumnType("date")
            .IsRequired();

        builder.Property(booking => booking.EndDate)
            .HasColumnType("date")
            .IsRequired();

        builder.Property(booking => booking.TotalPrice)
            .HasPrecision(18, 2)
            .IsRequired();

        builder.Property(booking => booking.Status)
            .IsRequired();

        builder.Property(booking => booking.ExpiresAt)
            .IsRequired();

        builder.Property(booking => booking.CreatedAt)
            .IsRequired();

        builder.Property(booking => booking.UpdatedAt)
            .IsRequired();

        builder.HasOne(booking => booking.Listing)
            .WithMany(listing => listing.Bookings)
            .HasForeignKey(booking => booking.ListingId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(booking => booking.Renter)
            .WithMany(user => user.Bookings)
            .HasForeignKey(booking => booking.RenterId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(booking => booking.RenterId);
        builder.HasIndex(booking => booking.ExpiresAt);
        builder.HasIndex(booking => new { booking.ListingId, booking.Status, booking.StartDate, booking.EndDate });
    }
}
