using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RentalPlatform.Domain.Entities;

namespace RentalPlatform.Infrastructure.Configurations;

public sealed class ToyReviewConfiguration : IEntityTypeConfiguration<ToyReview>
{
    public void Configure(EntityTypeBuilder<ToyReview> builder)
    {
        builder.ToTable("ToyReviews", t =>
        {
            t.HasCheckConstraint("CK_ToyReviews_Overall", "[OverallRating] >= 1 AND [OverallRating] <= 5");
            t.HasCheckConstraint("CK_ToyReviews_Condition", "[ConditionRating] >= 1 AND [ConditionRating] <= 5");
            t.HasCheckConstraint("CK_ToyReviews_Cleanliness", "[CleanlinessRating] >= 1 AND [CleanlinessRating] <= 5");
            t.HasCheckConstraint("CK_ToyReviews_Value", "[ValueForMoneyRating] >= 1 AND [ValueForMoneyRating] <= 5");
            t.HasCheckConstraint("CK_ToyReviews_Fun", "[FunPlayValueRating] >= 1 AND [FunPlayValueRating] <= 5");
            t.HasCheckConstraint("CK_ToyReviews_Description", "[DescriptionAccuracyRating] >= 1 AND [DescriptionAccuracyRating] <= 5");
        });

        builder.HasKey(r => r.Id);

        builder.Property(r => r.Comment).HasMaxLength(400);
        builder.Property(r => r.CreatedAt).IsRequired();

        // One toy review per booking.
        builder.HasIndex(r => r.BookingId).IsUnique();
        builder.HasIndex(r => r.ListingId);

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
    }
}
