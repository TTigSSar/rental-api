using Microsoft.Extensions.Logging;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.Common;
using RentalPlatform.Application.DTOs;
using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Infrastructure.Services;

/// <summary>
/// Auto-posts a ModerationNote into a member's Moderation conversation (get-or-created on
/// demand) when an admin takes a moderation action. Every method is best-effort: failures are
/// logged and swallowed so a chat problem can never break the moderation action that triggered
/// it — mirrors <see cref="ChatSystemMessageEmitter"/> exactly.
/// </summary>
public sealed class ModerationNoteEmitter : IModerationNoteEmitter
{
    private readonly IConversationsStore _store;
    private readonly IChatRealtimeNotifier _realtimeNotifier;
    private readonly ILogger<ModerationNoteEmitter> _logger;

    public ModerationNoteEmitter(
        IConversationsStore store,
        IChatRealtimeNotifier realtimeNotifier,
        ILogger<ModerationNoteEmitter> logger)
    {
        _store = store;
        _realtimeNotifier = realtimeNotifier;
        _logger = logger;
    }

    public Task ListingRejectedAsync(
        Guid moderatorId,
        Guid memberId,
        string listingTitle,
        string reasonLabel,
        string? note,
        CancellationToken cancellationToken = default)
    {
        var body = string.IsNullOrWhiteSpace(note)
            ? $"Your listing \"{listingTitle}\" was rejected: {reasonLabel}."
            : note.Trim();

        return EmitAsync(
            moderatorId, memberId, ModerationNoteKind.Reject, subject: listingTitle, reason: reasonLabel, body, cancellationToken);
    }

    public Task ListingRecategorisedAsync(
        Guid moderatorId,
        Guid memberId,
        string listingTitle,
        string fromCategoryName,
        string toCategoryName,
        CancellationToken cancellationToken = default)
    {
        var reason = $"{fromCategoryName} → {toCategoryName}";
        var body = $"Your listing \"{listingTitle}\" was moved to a different category: {reason}.";

        return EmitAsync(
            moderatorId, memberId, ModerationNoteKind.Category, subject: listingTitle, reason, body, cancellationToken);
    }

    public Task AccountSuspendedAsync(
        Guid moderatorId,
        Guid memberId,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        var trimmedReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        var body = trimmedReason is null
            ? "Your account has been suspended."
            : $"Your account has been suspended. Reason: {trimmedReason}.";

        return EmitAsync(
            moderatorId, memberId, ModerationNoteKind.Suspend, subject: null, trimmedReason, body, cancellationToken);
    }

    private async Task EmitAsync(
        Guid moderatorId,
        Guid memberId,
        ModerationNoteKind kind,
        string? subject,
        string? reason,
        string body,
        CancellationToken cancellationToken)
    {
        try
        {
            var conversation = await _store.GetOrCreateForModerationAsync(moderatorId, memberId, cancellationToken);
            var message = await _store.AddModerationNoteAsync(
                conversation.Id, moderatorId, kind, subject, reason, body, cancellationToken);

            var realtimeMessage = new ChatRealtimeMessage
            {
                Id = message.Id,
                ConversationId = conversation.Id,
                SenderId = moderatorId,
                SenderName = null,
                Type = ChatTokens.MessageTypeToken(MessageType.ModerationNote),
                SystemKind = null,
                NoteKind = ChatTokens.ModerationNoteKindToken(kind),
                NoteSubject = subject,
                NoteReason = reason,
                Body = body,
                AttachmentUrl = null,
                SentAt = message.CreatedAt
            };

            await _realtimeNotifier.MessageSentAsync(realtimeMessage, conversation.OwnerId, conversation.RenterId, cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Failed to emit {Kind} moderation note for member {MemberId}.",
                kind,
                memberId);
        }
    }
}
