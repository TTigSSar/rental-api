using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RentalPlatform.Domain.Entities;

namespace RentalPlatform.Infrastructure.Configurations;

public sealed class ReviewConfiguration : IEntityTypeConfiguration<Review>
{
    public void Configure(EntityTypeBuilder<Review> builder)
    {
        builder.ToTable("Reviews", t =>
            t.HasCheckConstraint("CK_Reviews_Rating", "[Rating] >= 1 AND [Rating] <= 5"));

        builder.HasKey(r => r.Id);

        builder.Property(r => r.ReviewerRole)
            .IsRequired();

        builder.Property(r => r.Rating)
            .IsRequired();

        builder.Property(r => r.Comment)
            .HasMaxLength(1000);

        builder.Property(r => r.CreatedAt)
            .IsRequired();

        // One review per booking per side (renter-side or owner-side).
        builder.HasIndex(r => new { r.BookingId, r.ReviewerRole })
            .IsUnique();

        builder.HasOne(r => r.Booking)
            .WithMany()
            .HasForeignKey(r => r.BookingId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(r => r.Listing)
            .WithMany()
            .HasForeignKey(r => r.ListingId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(r => r.Reviewer)
            .WithMany()
            .HasForeignKey(r => r.ReviewerId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(r => r.Reviewee)
            .WithMany()
            .HasForeignKey(r => r.RevieweeId)
            .OnDelete(DeleteBehavior.Restrict);

        // Efficient queries: reviews by listing, reviews received by a user.
        builder.HasIndex(r => r.ListingId);
        builder.HasIndex(r => r.RevieweeId);
    }
}
