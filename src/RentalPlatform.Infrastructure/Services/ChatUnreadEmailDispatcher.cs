using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.DTOs;
using RentalPlatform.Domain;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Infrastructure.Persistence;

namespace RentalPlatform.Infrastructure.Services;

// ADR-059: one delayed email per conversation per unread streak. A loss-tolerant periodic sweep (ADR-048
// amendment 2026-10-28), not an outbox: a missed run loses nothing because the next run sees the same unread
// lines. Skips 22:00-09:00 Yerevan, so what is still unread at 09:00 goes out then.
public sealed class ChatUnreadEmailDispatcher : BackgroundService
{
    public static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan UnreadThreshold = TimeSpan.FromMinutes(15);
    public const int MaxEmailsPerRun = 50;

    private const int QuietHoursStart = 22;
    private const int QuietHoursEnd = 9;
    private const string DevelopmentFallbackBaseUrl = "http://localhost:4200";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IChatEmailUnsubscribeTokens _tokens;
    private readonly TimeProvider _timeProvider;
    private readonly AppOptions _app;
    private readonly ILogger<ChatUnreadEmailDispatcher> _logger;

    public ChatUnreadEmailDispatcher(
        IServiceScopeFactory scopeFactory,
        IChatEmailUnsubscribeTokens tokens,
        TimeProvider timeProvider,
        IOptions<AppOptions> app,
        ILogger<ChatUnreadEmailDispatcher> logger)
    {
        _scopeFactory = scopeFactory;
        _tokens = tokens;
        _timeProvider = timeProvider;
        _app = app.Value;
        _logger = logger;
    }

    /// <summary>True from 22:00 until 09:00 Yerevan (fixed UTC+4, ADR-048).</summary>
    public static bool IsQuietHours(DateTimeOffset utcNow)
    {
        var hour = (utcNow.UtcDateTime + YerevanCalendar.Offset).Hour;
        return hour >= QuietHoursStart || hour < QuietHoursEnd;
    }

    /// <summary>One sweep. Returns how many emails were handed to the email service.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var nowOffset = _timeProvider.GetUtcNow();
        if (IsQuietHours(nowOffset))
        {
            return 0;
        }

        var now = nowOffset.UtcDateTime;
        var cutoff = now - UnreadThreshold;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var emailService = scope.ServiceProvider.GetRequiredService<IEmailService>();

        // The unread definition is the one ConversationsStore.ListForUserAsync uses (ADR-001): after the
        // participant's read cursor, not their own message, not a line they caused (ActorUserId).
        // Booking threads only: a Moderation thread has no toy and its participants are support (ADR-049).
        var candidates = await (
            from participant in db.ConversationParticipants.AsNoTracking()
            join conversation in db.Conversations.AsNoTracking() on participant.ConversationId equals conversation.Id
            join user in db.Users.AsNoTracking() on participant.UserId equals user.Id
            where conversation.Kind == ConversationKind.Booking
                && (participant.UserId == conversation.RenterId || participant.UserId == conversation.OwnerId)
                && user.ChatEmailsEnabled
                && user.IsEmailConfirmed
                && !user.IsBlocked
                && user.DeletedAt == null
                && user.Role != UserRole.Admin
            let oldestUnread = db.ChatMessages
                .Where(message => message.ConversationId == conversation.Id
                    && message.SenderId != participant.UserId
                    && (message.ActorUserId == null || message.ActorUserId != participant.UserId)
                    && (participant.LastReadAt == null || message.CreatedAt > participant.LastReadAt))
                .Min(message => (DateTime?)message.CreatedAt)
            // One email per unread streak. Re-armed when the recipient has read the conversation since the last
            // email, i.e. the oldest unread line is newer than the email. This is not written as
            // "LastReadAt > ChatEmailSentAt": MarkReadAsync sets LastReadAt to the latest message's CreatedAt,
            // not to the time of the read, so after a real read LastReadAt is older than the email and that
            // comparison would never re-arm. Whenever LastReadAt > ChatEmailSentAt this holds too.
            where oldestUnread != null && oldestUnread < cutoff
                && (participant.ChatEmailSentAt == null || oldestUnread > participant.ChatEmailSentAt)
            orderby oldestUnread
            select new Candidate(
                participant.Id,
                participant.ConversationId,
                participant.UserId,
                participant.LastReadAt,
                participant.ChatEmailSentAt,
                user.Email,
                user.PreferredLanguage,
                conversation.ToyTitle,
                conversation.ToyImageUrl))
            .Take(MaxEmailsPerRun)
            .ToListAsync(cancellationToken);

        var sent = 0;
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Claim BEFORE sending (at-most-once, ADR-029): the update only matches while the row still holds
            // the value the candidate was selected with, so a concurrent run cannot claim it twice.
            var claimed = await db.ConversationParticipants
                .Where(participant => participant.Id == candidate.ParticipantId
                    && participant.ChatEmailSentAt == candidate.ChatEmailSentAt)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(participant => participant.ChatEmailSentAt, now),
                    cancellationToken);
            if (claimed == 0)
            {
                continue;
            }

            try
            {
                var email = await BuildEmailAsync(db, candidate, cancellationToken);
                await emailService.SendChatUnreadAsync(email, cancellationToken);
                sent++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // Not retried (the claim stands). No address and no token in the log.
                _logger.LogError(
                    "Chat unread email for conversation {ConversationId} could not be prepared ({ExceptionType}).",
                    candidate.ConversationId,
                    exception.GetType().Name);
            }
        }

        if (sent > 0)
        {
            _logger.LogInformation("Sent {Count} chat unread email(s).", sent);
        }

        return sent;
    }

    private async Task<ChatUnreadEmail> BuildEmailAsync(AppDbContext db, Candidate candidate, CancellationToken cancellationToken)
    {
        // The human (non-system) sender of the most recent unread message, if any. System lines have a
        // null SenderId; only a human line decides the "<name> wrote to you" heading.
        var humanSender = await (
            from message in db.ChatMessages.AsNoTracking()
            join sender in db.Users.AsNoTracking() on message.SenderId equals sender.Id
            where message.ConversationId == candidate.ConversationId
                && message.Type != MessageType.System
                && message.SenderId != candidate.UserId
                && (message.ActorUserId == null || message.ActorUserId != candidate.UserId)
                && (candidate.LastReadAt == null || message.CreatedAt > candidate.LastReadAt)
            orderby message.CreatedAt descending
            select new { sender.FirstName, IsDeleted = sender.DeletedAt != null })
            .FirstOrDefaultAsync(cancellationToken);

        var baseUrl = (string.IsNullOrWhiteSpace(_app.PublicBaseUrl) ? DevelopmentFallbackBaseUrl : _app.PublicBaseUrl.Trim())
            .TrimEnd('/');

        return new ChatUnreadEmail(
            To: candidate.Email,
            PreferredLanguage: candidate.PreferredLanguage,
            ToyTitle: candidate.ToyTitle ?? string.Empty,
            ToyImageUrl: AbsoluteImageUrl(baseUrl, candidate.ToyImageUrl),
            HasHumanMessage: humanSender is not null,
            SenderFirstName: humanSender is { IsDeleted: false } ? humanSender.FirstName : null,
            SenderDeleted: humanSender?.IsDeleted ?? false,
            OpenChatUrl: $"{baseUrl}/chat/{candidate.ConversationId}",
            UnsubscribeUrl: $"{baseUrl}/unsubscribe/chat-emails?t={_tokens.Issue(candidate.UserId)}",
            SettingsUrl: $"{baseUrl}/profile/settings");
    }

    // Stored listing image URLs are served by the app itself; an email needs an absolute one.
    private static string? AbsoluteImageUrl(string baseUrl, string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var trimmed = url.Trim();
        return Uri.TryCreate(trimmed, UriKind.Absolute, out var absolute)
            && (absolute.Scheme == Uri.UriSchemeHttps || absolute.Scheme == Uri.UriSchemeHttp)
            ? trimmed
            : $"{baseUrl}/{trimmed.TrimStart('/')}";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SweepInterval);
        try
        {
            // First run after one interval, not at startup: nothing is urgent and startup stays quiet.
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await RunOnceAsync(stoppingToken);
                }
                catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                {
                    // A transient failure must not tear down the host; the next tick sees the same state.
                    _logger.LogError(exception, "Chat unread email sweep failed.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    private sealed record Candidate(
        Guid ParticipantId,
        Guid ConversationId,
        Guid UserId,
        DateTime? LastReadAt,
        DateTime? ChatEmailSentAt,
        string Email,
        string? PreferredLanguage,
        string? ToyTitle,
        string? ToyImageUrl);
}
