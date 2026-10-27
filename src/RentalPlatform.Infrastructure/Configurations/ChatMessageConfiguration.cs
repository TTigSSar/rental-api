using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RentalPlatform.Domain.Entities;

namespace RentalPlatform.Infrastructure.Configurations;

public sealed class ChatMessageConfiguration : IEntityTypeConfiguration<ChatMessage>
{
    public void Configure(EntityTypeBuilder<ChatMessage> builder)
    {
        builder.ToTable("ChatMessages");

        builder.HasKey(message => message.Id);

        builder.Property(message => message.Type)
            .IsRequired();

        builder.Property(message => message.Body)
            .HasMaxLength(4000);

        builder.Property(message => message.AttachmentUrl)
            .HasMaxLength(1000);

        builder.Property(message => message.NoteSubject)
            .HasMaxLength(200);

        builder.Property(message => message.NoteReason)
            .HasMaxLength(200);

        builder.Property(message => message.MeetingPointLatitude)
            .HasColumnType("decimal(9,6)");

        builder.Property(message => message.MeetingPointLongitude)
            .HasColumnType("decimal(9,6)");

        builder.Property(message => message.CreatedAt)
            .IsRequired();

        builder.HasOne(message => message.Conversation)
            .WithMany(conversation => conversation.Messages)
            .HasForeignKey(message => message.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);

        // Null for system messages; no inverse navigation on User.
        builder.HasOne(message => message.Sender)
            .WithMany()
            .HasForeignKey(message => message.SenderId)
            .OnDelete(DeleteBehavior.Restrict);

        // Null for system/timer lines and pre-RC-5 rows; no navigation, same shape as Sender.
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(message => message.ActorUserId)
            .OnDelete(DeleteBehavior.Restrict);

        // Paged thread reads: newest-per-conversation.
        builder.HasIndex(message => new { message.ConversationId, message.CreatedAt });
    }
}
