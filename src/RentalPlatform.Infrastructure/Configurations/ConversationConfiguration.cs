using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RentalPlatform.Domain.Entities;

namespace RentalPlatform.Infrastructure.Configurations;

public sealed class ConversationConfiguration : IEntityTypeConfiguration<Conversation>
{
    public void Configure(EntityTypeBuilder<Conversation> builder)
    {
        builder.ToTable("Conversations");

        builder.HasKey(conversation => conversation.Id);

        builder.Property(conversation => conversation.ToyTitle)
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(conversation => conversation.ToyImageUrl)
            .HasMaxLength(1000);

        builder.Property(conversation => conversation.LastMessageSnippet)
            .HasMaxLength(500);

        builder.Property(conversation => conversation.CreatedAt)
            .IsRequired();

        // One conversation per booking (see ADR-001).
        builder.HasOne(conversation => conversation.Booking)
            .WithMany()
            .HasForeignKey(conversation => conversation.BookingId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(conversation => conversation.Owner)
            .WithMany()
            .HasForeignKey(conversation => conversation.OwnerId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(conversation => conversation.Renter)
            .WithMany()
            .HasForeignKey(conversation => conversation.RenterId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(conversation => conversation.BookingId).IsUnique();
        // Inbox reads filter by participant and order by recency.
        builder.HasIndex(conversation => new { conversation.OwnerId, conversation.LastMessageAt });
        builder.HasIndex(conversation => new { conversation.RenterId, conversation.LastMessageAt });
    }
}
