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
            .HasMaxLength(200);

        builder.Property(conversation => conversation.ToyImageUrl)
            .HasMaxLength(1000);

        builder.Property(conversation => conversation.LastMessageSnippet)
            .HasMaxLength(500);

        builder.Property(conversation => conversation.Kind)
            .IsRequired();

        builder.Property(conversation => conversation.CreatedAt)
            .IsRequired();

        // One conversation per booking (see ADR-001) — optional now that a Moderation thread
        // (Kind == Moderation) has no booking at all.
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

        // SQL Server's plain unique index permits only one NULL row; a filtered index restricts
        // the uniqueness constraint to actual bookings so a second Moderation thread (BookingId
        // null) can insert.
        builder.HasIndex(conversation => conversation.BookingId)
            .IsUnique()
            .HasFilter("[BookingId] IS NOT NULL");
        // Inbox reads filter by participant and order by recency.
        builder.HasIndex(conversation => new { conversation.OwnerId, conversation.LastMessageAt });
        builder.HasIndex(conversation => new { conversation.RenterId, conversation.LastMessageAt });
        // Admin Messages screen: look up (or get-or-create) the single Moderation thread for a
        // member. Unique (filtered to Kind == Moderation, i.e. 1) so a member can never end up
        // with two Moderation threads — mirrors the BookingId filtered unique index above, which
        // guards the same get-or-create race on the Booking side. The filter can't reference
        // RenterId (it must always be non-null and isn't itself the discriminator), so it filters
        // on Kind alone; combined with the composite key that still constrains RenterId to be
        // unique within Moderation rows without touching Booking rows (Kind == 0), which keep
        // allowing many rows per RenterId (one per booking).
        builder.HasIndex(conversation => new { conversation.Kind, conversation.RenterId })
            .IsUnique()
            .HasFilter("[Kind] = 1");
    }
}
