using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.Common;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Infrastructure.Persistence;

namespace RentalPlatform.Infrastructure.Services;

// ADR-067 section 5: ONE long-running consumer of the in-memory queue, a fresh DI scope per job (the DbContext
// is scoped, M-016/ADR-048). Loss-tolerant: no retry, a failed push is logged and forgotten.
public sealed class ChatPushDispatcher : BackgroundService
{
    // ADR-067 section 10: the push service keeps an undelivered message for 24 h.
    public const int TimeToLiveSeconds = 24 * 60 * 60;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ChatPushQueue _queue;
    private readonly IWebPushSettings _settings;
    private readonly ILogger<ChatPushDispatcher> _logger;

    public ChatPushDispatcher(
        IServiceScopeFactory scopeFactory,
        ChatPushQueue queue,
        IWebPushSettings settings,
        ILogger<ChatPushDispatcher> logger)
    {
        _scopeFactory = scopeFactory;
        _queue = queue;
        _settings = settings;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var job in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await ProcessAsync(job, stoppingToken);
                }
                catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                {
                    // One bad job must not stop the consumer.
                    _logger.LogError(exception, "Chat push for conversation {ConversationId} failed.", job.ConversationId);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    /// <summary>Sends one job. Returns how many pushes the push services accepted.</summary>
    public async Task<int> ProcessAsync(ChatPushJob job, CancellationToken cancellationToken = default)
    {
        if (!_settings.IsEnabled)
        {
            return 0;
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var sender = scope.ServiceProvider.GetRequiredService<IPushSender>();

        var conversation = await db.Conversations.AsNoTracking()
            .Where(candidate => candidate.Id == job.ConversationId)
            .Select(candidate => new { candidate.Kind, candidate.ToyTitle, candidate.OwnerId, candidate.RenterId })
            .FirstOrDefaultAsync(cancellationToken);
        if (conversation is null)
        {
            return 0;
        }

        var isModeration = conversation.Kind == ConversationKind.Moderation;

        // Same recipients as the chat email (ADR-059): booking thread = renter and owner; moderation thread = the
        // member only (RenterId by convention, the moderator is support). Never the sender or actor; not blocked,
        // not deleted, not Admin. No ChatEmailsEnabled check (email-only) and no quiet hours (ADR-067 section 3).
        var memberIds = isModeration
            ? new[] { conversation.RenterId }
            : new[] { conversation.RenterId, conversation.OwnerId };

        var recipients = await db.Users.AsNoTracking()
            .Where(user => memberIds.Contains(user.Id)
                && user.Id != job.SenderId
                && !user.IsBlocked
                && user.DeletedAt == null
                && user.Role != UserRole.Admin)
            .Select(user => new { user.Id, user.PreferredLanguage })
            .ToListAsync(cancellationToken);
        if (recipients.Count == 0)
        {
            return 0;
        }

        var senderRow = isModeration
            ? null
            : await db.Users.AsNoTracking()
                .Where(user => user.Id == job.SenderId)
                .Select(user => new { user.FirstName, IsDeleted = user.DeletedAt != null })
                .FirstOrDefaultAsync(cancellationToken);

        var recipientIds = recipients.Select(recipient => recipient.Id).ToList();
        var subscriptions = await db.PushSubscriptions.AsNoTracking()
            .Where(subscription => recipientIds.Contains(subscription.UserId))
            .Select(subscription => new { subscription.Id, subscription.UserId, subscription.Endpoint, subscription.P256dh, subscription.Auth })
            .ToListAsync(cancellationToken);

        var topic = job.ConversationId.ToString("N");
        var delivered = 0;
        var stale = new List<Guid>();

        foreach (var subscription in subscriptions)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Re-validated at send time (ADR-067 section 8): a row written under an older rule is never used.
            if (!PushEndpointValidator.IsValidSubscription(subscription.Endpoint, subscription.P256dh, subscription.Auth))
            {
                stale.Add(subscription.Id);
                continue;
            }

            var language = recipients.First(recipient => recipient.Id == subscription.UserId).PreferredLanguage;
            var payload = BuildPayload(job.ConversationId, language, isModeration, conversation.ToyTitle, senderRow?.FirstName, senderRow?.IsDeleted ?? false);

            var outcome = await sender.SendAsync(
                new PushDelivery(subscription.Endpoint, subscription.P256dh, subscription.Auth, payload, topic, TimeToLiveSeconds),
                cancellationToken);

            if (outcome == PushSendOutcome.Gone)
            {
                stale.Add(subscription.Id);
            }
            else if (outcome == PushSendOutcome.Sent)
            {
                delivered++;
            }
        }

        if (stale.Count > 0)
        {
            await db.PushSubscriptions
                .Where(subscription => stale.Contains(subscription.Id))
                .ExecuteDeleteAsync(cancellationToken);
        }

        return delivered;
    }

    // ADR-067 section 2: sender's first name + toy title, never the message text; moderation thread gets the
    // neutral heading. Rendered in the recipient's language (ADR-025), English fallback.
    internal static string BuildPayload(
        Guid conversationId,
        string? language,
        bool isModeration,
        string? toyTitle,
        string? senderFirstName,
        bool senderDeleted)
    {
        string title;
        string body;
        if (isModeration)
        {
            title = "DoRent";
            body = ChatUnreadEmailTemplate.ModerationHeadingFor(language);
        }
        else
        {
            title = senderDeleted || string.IsNullOrWhiteSpace(senderFirstName)
                ? ChatUnreadEmailTemplate.DeletedUserFor(language)
                : senderFirstName.Trim();
            body = NewMessageAbout(language, toyTitle ?? string.Empty);
        }

        return JsonSerializer.Serialize(new
        {
            title,
            body,
            conversationId = conversationId.ToString("D"),
            url = $"/chat/{conversationId:D}"
        });
    }

    // The email heading "<name> wrote to you about <toy>" without the name (same wording, ADR-059).
    private static string NewMessageAbout(string? language, string toyTitle)
    {
        var code = NotificationCopy.ResolveLanguage(language);
        return code switch
        {
            "ru" => $"Новое сообщение об игрушке «{toyTitle}»",
            "hy" => $"Նոր հաղորդագրություն «{toyTitle}» խաղալիքի մասին",
            _ => $"New message about «{toyTitle}»"
        };
    }
}
