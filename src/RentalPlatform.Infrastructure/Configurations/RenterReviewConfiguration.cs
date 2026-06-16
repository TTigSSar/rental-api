using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RentalPlatform.Domain.Entities;

namespace RentalPlatform.Infrastructure.Configurations;

public sealed class RenterReviewConfiguration : IEntityTypeConfiguration<RenterReview>
{
    public void Configure(EntityTypeBuilder<RenterReview> builder)
    {
        builder.ToTable("RenterReviews", t =>
        {
            t.HasCheckConstraint("CK_RenterReviews_Communication", "[CommunicationRating] >= 1 AND [CommunicationRating] <= 5");
            t.HasCheckConstraint("CK_RenterReviews_Returned", "[ReturnedOnTimeRating] >= 1 AND [ReturnedOnTimeRating] <= 5");
            t.HasCheckConstraint("CK_RenterReviews_Care", "[CareOfToyRating] >= 1 AND [CareOfToyRating] <= 5");
            t.HasCheckConstraint("CK_RenterReviews_WouldRent", "[WouldRentAgainRating] >= 1 AND [WouldRentAgainRating] <= 5");
        });

        builder.HasKey(r => r.Id);

        builder.Property(r => r.Comment).HasMaxLength(400);
        builder.Property(r => r.CreatedAt).IsRequired();

        // One renter review per booking.
        builder.HasIndex(r => r.BookingId).IsUnique();
        builder.HasIndex(r => r.RenterId);

        builder.HasOne(r => r.Booking)
            .WithMany()
            .HasForeignKey(r => r.BookingId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(r => r.Renter)
            .WithMany()
            .HasForeignKey(r => r.RenterId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(r => r.Reviewer)
            .WithMany()
            .HasForeignKey(r => r.ReviewerId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
