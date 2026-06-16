using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RentalPlatform.Domain.Entities;

namespace RentalPlatform.Infrastructure.Configurations;

public sealed class OwnerReviewConfiguration : IEntityTypeConfiguration<OwnerReview>
{
    public void Configure(EntityTypeBuilder<OwnerReview> builder)
    {
        builder.ToTable("OwnerReviews", t =>
        {
            t.HasCheckConstraint("CK_OwnerReviews_Communication", "[CommunicationRating] >= 1 AND [CommunicationRating] <= 5");
            t.HasCheckConstraint("CK_OwnerReviews_Pickup", "[PickupHandoverRating] >= 1 AND [PickupHandoverRating] <= 5");
            t.HasCheckConstraint("CK_OwnerReviews_Friendliness", "[FriendlinessRating] >= 1 AND [FriendlinessRating] <= 5");
        });

        builder.HasKey(r => r.Id);

        builder.Property(r => r.Comment).HasMaxLength(400);
        builder.Property(r => r.CreatedAt).IsRequired();

        // One owner review per booking.
        builder.HasIndex(r => r.BookingId).IsUnique();
        builder.HasIndex(r => r.OwnerId);

        builder.HasOne(r => r.Booking)
            .WithMany()
            .HasForeignKey(r => r.BookingId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(r => r.Owner)
            .WithMany()
            .HasForeignKey(r => r.OwnerId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(r => r.Reviewer)
            .WithMany()
            .HasForeignKey(r => r.ReviewerId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
