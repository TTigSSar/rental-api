using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.DTOs;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Infrastructure.Persistence;
using RentalPlatform.Infrastructure.Services;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Chat;

// ADR-059: candidate selection, quiet hours, one email per unread streak. Runs the REAL dispatcher over the
// REAL schema on SQLite (M-013); the unread definition is cross-checked against the real
// ConversationsStore, and the re-arm test drives the real MarkReadAsync (which sets the read cursor to the
// latest message's CreatedAt, not to the time of the read).
public sealed class ChatUnreadEmailDispatcherTests
{
    private static readonly Guid OwnerId = new("f0000000-0000-0000-0000-000000000001");
    private static readonly Guid RenterId = new("f0000000-0000-0000-0000-000000000002");
    private static readonly Guid CategoryId = new("f0000000-0000-0000-0000-000000000003");
    private static readonly Guid ListingId = new("f0000000-0000-0000-0000-000000000004");
    private static readonly Guid BookingId = new("f0000000-0000-0000-0000-000000000005");
    private static readonly Guid ConversationId = new("f0000000-0000-0000-0000-000000000006");

    // 10:00 UTC = 14:00 in Yerevan: outside quiet hours.
    private static readonly DateTimeOffset Noon = new(2026, 11, 7, 10, 0, 0, TimeSpan.Zero);

    private const string JwtSecret = "xunit-dispatcher-test-signing-key-0123456789";

    private sealed class Harness : IDisposable
    {
        public SqliteTestDatabase Db { get; } = new();
        public FakeEmailService Email { get; } = new();
        public FakeTimeProvider Clock { get; } = new(Noon);
        public ChatEmailUnsubscribeTokens Tokens { get; }
        public ChatUnreadEmailDispatcher Dispatcher { get; }

        public Harness(IEmailService? emailOverride = null)
        {
            Tokens = new ChatEmailUnsubscribeTokens(Options.Create(new JwtOptions { SecretKey = JwtSecret }), Clock);

            var services = new ServiceCollection();
            services.AddScoped(_ => Db.CreateContext());
            services.AddSingleton(emailOverride ?? Email);
            var provider = services.BuildServiceProvider();

            Dispatcher = new ChatUnreadEmailDispatcher(
                provider.GetRequiredService<IServiceScopeFactory>(),
                Tokens,
                Clock,
                Options.Create(new AppOptions { PublicBaseUrl = "https://dorent.test/" }),
                NullLogger<ChatUnreadEmailDispatcher>.Instance);
        }

        public async Task SeedConversationAsync(
            Action<User, User>? configure = null,
            ConversationKind kind = ConversationKind.Booking)
        {
            var owner = TestData.User(OwnerId, "owner@test.local", firstName: "Olga", preferredLanguage: "en");
            var renter = TestData.User(RenterId, "renter@test.local", firstName: "Ruben", preferredLanguage: "ru");
            configure?.Invoke(owner, renter);
            await Db.SeedAsync(owner, renter, TestData.Category(CategoryId));
            await Db.SeedAsync(TestData.Listing(ListingId, OwnerId, CategoryId));
            await Db.SeedAsync(TestData.Booking(
                BookingId, ListingId, RenterId, TestData.Today.AddDays(3), TestData.Today.AddDays(5), BookingStatus.Approved));

            var conversation = TestData.Conversation(ConversationId, BookingId, OwnerId, RenterId);
            conversation.Kind = kind;
            conversation.ToyImageUrl = "/images/listings/toy.jpg";
            await Db.SeedAsync(conversation);
            await Db.SeedAsync(
                new ConversationParticipant { Id = Guid.NewGuid(), ConversationId = ConversationId, UserId = OwnerId },
                new ConversationParticipant { Id = Guid.NewGuid(), ConversationId = ConversationId, UserId = RenterId });
        }

        public async Task<ChatMessage> AddLineAsync(
            Guid? senderId,
            TimeSpan age,
            MessageType type = MessageType.Text,
            Guid? actorUserId = null)
        {
            var message = TestData.ChatMessage(Guid.NewGuid(), ConversationId, senderId, type: type);
            message.CreatedAt = Clock.GetUtcNow().UtcDateTime - age;
            message.ActorUserId = actorUserId;
            if (type == MessageType.System)
            {
                message.SystemKind = ChatSystemKind.Approved;
                message.Body = null;
            }

            await Db.SeedAsync(message);
            return message;
        }

        public async Task<ConversationParticipant> ParticipantAsync(Guid userId)
        {
            await using var context = Db.CreateContext();
            return await context.ConversationParticipants.AsNoTracking()
                .SingleAsync(participant => participant.ConversationId == ConversationId && participant.UserId == userId);
        }

        public void Dispose() => Db.Dispose();
    }

    private static readonly TimeSpan Old = TimeSpan.FromMinutes(16);

    // ---- threshold -----------------------------------------------------------------------------

    [Fact]
    public async Task Waits_Until_The_Oldest_Unread_Line_Is_Older_Than_15_Minutes()
    {
        using var h = new Harness();
        await h.SeedConversationAsync();
        await h.AddLineAsync(OwnerId, TimeSpan.FromMinutes(14));

        Assert.Equal(0, await h.Dispatcher.RunOnceAsync());
        Assert.Empty(h.Email.ChatUnreadSent);
        Assert.Null((await h.ParticipantAsync(RenterId)).ChatEmailSentAt);

        h.Clock.Advance(TimeSpan.FromMinutes(2));

        Assert.Equal(1, await h.Dispatcher.RunOnceAsync());
        var email = Assert.Single(h.Email.ChatUnreadSent);
        Assert.Equal("renter@test.local", email.To);
    }

    [Fact]
    public async Task The_Threshold_Is_Measured_On_The_Oldest_Unread_Line_Not_The_Newest()
    {
        using var h = new Harness();
        await h.SeedConversationAsync();
        await h.AddLineAsync(OwnerId, TimeSpan.FromMinutes(20));
        await h.AddLineAsync(OwnerId, TimeSpan.FromMinutes(1));

        Assert.Equal(1, await h.Dispatcher.RunOnceAsync());
    }

    // ---- who is a recipient --------------------------------------------------------------------

    [Fact]
    public async Task Only_The_Other_Party_Is_Emailed_For_A_Human_Message()
    {
        using var h = new Harness();
        await h.SeedConversationAsync();
        await h.AddLineAsync(OwnerId, Old);

        await h.Dispatcher.RunOnceAsync();

        var email = Assert.Single(h.Email.ChatUnreadSent);
        Assert.Equal("renter@test.local", email.To);
        Assert.NotNull((await h.ParticipantAsync(RenterId)).ChatEmailSentAt);
        Assert.Null((await h.ParticipantAsync(OwnerId)).ChatEmailSentAt);
    }

    [Fact]
    public async Task A_Line_The_Recipient_Caused_Is_Never_Unread_For_Them()
    {
        using var h = new Harness();
        await h.SeedConversationAsync();
        // The renter's own action produced this system line (RC-5): unread for the owner only.
        await h.AddLineAsync(null, Old, MessageType.System, actorUserId: RenterId);

        await h.Dispatcher.RunOnceAsync();

        var email = Assert.Single(h.Email.ChatUnreadSent);
        Assert.Equal("owner@test.local", email.To);
    }

    [Fact]
    public async Task A_System_Line_Counts_And_Gives_The_Neutral_Heading()
    {
        using var h = new Harness();
        await h.SeedConversationAsync();
        await h.AddLineAsync(null, Old, MessageType.System);

        await h.Dispatcher.RunOnceAsync();

        // No actor: unread for both parties.
        Assert.Equal(2, h.Email.ChatUnreadSent.Count);
        Assert.All(h.Email.ChatUnreadSent, email =>
        {
            Assert.False(email.HasHumanMessage);
            Assert.Null(email.SenderFirstName);
        });
    }

    [Fact]
    public async Task The_Dispatcher_Agrees_With_The_Real_Unread_Count()
    {
        using var h = new Harness();
        await h.SeedConversationAsync();
        await h.AddLineAsync(OwnerId, Old);
        await h.AddLineAsync(null, Old, MessageType.System, actorUserId: RenterId);

        await using var context = h.Db.CreateContext();
        var store = new ConversationsStore(context, NullLogger<ConversationsStore>.Instance);
        var renterUnread = (await store.ListForUserAsync(RenterId)).Sum(item => item.UnreadCount);
        var ownerUnread = (await store.ListForUserAsync(OwnerId)).Sum(item => item.UnreadCount);
        Assert.Equal(1, renterUnread);
        Assert.Equal(1, ownerUnread);

        await h.Dispatcher.RunOnceAsync();

        Assert.Equal(
            new[] { "owner@test.local", "renter@test.local" },
            h.Email.ChatUnreadSent.Select(email => email.To).OrderBy(to => to).ToArray());
    }

    [Theory]
    [InlineData("opted-out")]
    [InlineData("unverified")]
    [InlineData("blocked")]
    [InlineData("deleted")]
    [InlineData("admin")]
    public async Task An_Ineligible_Recipient_Is_Skipped_And_Not_Claimed(string reason)
    {
        using var h = new Harness();
        await h.SeedConversationAsync((_, renter) =>
        {
            switch (reason)
            {
                case "opted-out": renter.ChatEmailsEnabled = false; break;
                case "unverified": renter.IsEmailConfirmed = false; break;
                case "blocked": renter.IsBlocked = true; break;
                case "deleted": renter.DeletedAt = DateTime.UtcNow; break;
                case "admin": renter.Role = UserRole.Admin; break;
            }
        });
        await h.AddLineAsync(OwnerId, Old);

        Assert.Equal(0, await h.Dispatcher.RunOnceAsync());
        Assert.Empty(h.Email.ChatUnreadSent);
        Assert.Null((await h.ParticipantAsync(RenterId)).ChatEmailSentAt);
    }

    [Fact]
    public async Task A_Moderation_Thread_Is_Never_Emailed()
    {
        using var h = new Harness();
        await h.SeedConversationAsync(kind: ConversationKind.Moderation);
        await h.AddLineAsync(OwnerId, Old);

        Assert.Equal(0, await h.Dispatcher.RunOnceAsync());
    }

    [Fact]
    public async Task Opt_Out_Written_Explicitly_Is_Persisted_As_False()
    {
        // HasDefaultValue(true) alone would store true for an explicit false on insert.
        using var h = new Harness();
        await h.SeedConversationAsync((_, renter) => renter.ChatEmailsEnabled = false);

        await using var context = h.Db.CreateContext();
        Assert.False((await context.Users.AsNoTracking().SingleAsync(user => user.Id == RenterId)).ChatEmailsEnabled);
        Assert.True((await context.Users.AsNoTracking().SingleAsync(user => user.Id == OwnerId)).ChatEmailsEnabled);
    }

    // ---- one per streak ------------------------------------------------------------------------

    [Fact]
    public async Task One_Email_Per_Unread_Streak()
    {
        using var h = new Harness();
        await h.SeedConversationAsync();
        await h.AddLineAsync(OwnerId, Old);

        Assert.Equal(1, await h.Dispatcher.RunOnceAsync());
        await h.AddLineAsync(OwnerId, TimeSpan.Zero);
        h.Clock.Advance(TimeSpan.FromMinutes(30));

        Assert.Equal(0, await h.Dispatcher.RunOnceAsync());
        Assert.Single(h.Email.ChatUnreadSent);
    }

    [Fact]
    public async Task The_Streak_Is_Re_Armed_After_A_Real_Read_And_A_New_Unread_Line()
    {
        using var h = new Harness();
        await h.SeedConversationAsync();
        var first = await h.AddLineAsync(OwnerId, Old);
        Assert.Equal(1, await h.Dispatcher.RunOnceAsync());

        // The renter reads the chat after the email, through the real store: the cursor moves to the
        // latest message's CreatedAt (older than the email), not to the time of the read.
        h.Clock.Advance(TimeSpan.FromMinutes(5));
        await using (var context = h.Db.CreateContext())
        {
            var store = new ConversationsStore(context, NullLogger<ConversationsStore>.Instance);
            await store.MarkReadAsync(ConversationId, RenterId);
        }

        Assert.True((await h.ParticipantAsync(RenterId)).LastReadAt <= (await h.ParticipantAsync(RenterId)).ChatEmailSentAt);

        // Read, nothing new: no email.
        h.Clock.Advance(TimeSpan.FromMinutes(20));
        Assert.Equal(0, await h.Dispatcher.RunOnceAsync());

        // Something new, still unread after 15 minutes: a second email.
        await h.AddLineAsync(OwnerId, TimeSpan.FromMinutes(1));
        h.Clock.Advance(TimeSpan.FromMinutes(16));
        Assert.Equal(1, await h.Dispatcher.RunOnceAsync());
        Assert.Equal(2, h.Email.ChatUnreadSent.Count);
        Assert.Equal(first.CreatedAt, (await h.ParticipantAsync(RenterId)).LastReadAt);
    }

    [Fact]
    public async Task A_New_Line_Without_A_Read_Does_Not_Re_Arm()
    {
        using var h = new Harness();
        await h.SeedConversationAsync();
        await h.AddLineAsync(OwnerId, Old);
        await h.Dispatcher.RunOnceAsync();

        h.Clock.Advance(TimeSpan.FromHours(1));
        await h.AddLineAsync(OwnerId, TimeSpan.FromMinutes(30));

        Assert.Equal(0, await h.Dispatcher.RunOnceAsync());
    }

    [Fact]
    public async Task A_Failed_Send_Is_Not_Retried()
    {
        var thrower = new ThrowingChatEmailService();
        using var h = new Harness(thrower);
        await h.SeedConversationAsync();
        await h.AddLineAsync(OwnerId, Old);

        await h.Dispatcher.RunOnceAsync();
        Assert.Equal(1, thrower.Attempts);
        Assert.NotNull((await h.ParticipantAsync(RenterId)).ChatEmailSentAt);

        await h.Dispatcher.RunOnceAsync();
        Assert.Equal(1, thrower.Attempts);
    }

    [Fact]
    public async Task A_Run_Sends_At_Most_Fifty_Emails_And_The_Rest_Go_Next_Run()
    {
        using var h = new Harness();
        await h.SeedConversationAsync();

        var ownerId = OwnerId;
        for (var i = 0; i < ChatUnreadEmailDispatcher.MaxEmailsPerRun + 5; i++)
        {
            var renterId = Guid.NewGuid();
            var bookingId = Guid.NewGuid();
            var conversationId = Guid.NewGuid();
            await h.Db.SeedAsync(TestData.User(renterId, $"r{i}@cap.local"));
            await h.Db.SeedAsync(TestData.Booking(
                bookingId, ListingId, renterId, TestData.Today.AddDays(10 + i), TestData.Today.AddDays(11 + i), BookingStatus.Approved));
            await h.Db.SeedAsync(TestData.Conversation(conversationId, bookingId, ownerId, renterId));
            await h.Db.SeedAsync(
                new ConversationParticipant { Id = Guid.NewGuid(), ConversationId = conversationId, UserId = renterId },
                new ConversationParticipant { Id = Guid.NewGuid(), ConversationId = conversationId, UserId = ownerId });
            var message = TestData.ChatMessage(Guid.NewGuid(), conversationId, ownerId);
            message.CreatedAt = Noon.UtcDateTime - Old;
            await h.Db.SeedAsync(message);
        }

        Assert.Equal(ChatUnreadEmailDispatcher.MaxEmailsPerRun, await h.Dispatcher.RunOnceAsync());
        Assert.Equal(5, await h.Dispatcher.RunOnceAsync());
        Assert.Equal(0, await h.Dispatcher.RunOnceAsync());
    }

    // ---- quiet hours ---------------------------------------------------------------------------

    [Theory]
    [InlineData(17, 59, false)] // 21:59 Yerevan
    [InlineData(18, 0, true)]   // 22:00
    [InlineData(23, 30, true)]  // 03:30
    [InlineData(4, 59, true)]   // 08:59
    [InlineData(5, 0, false)]   // 09:00
    [InlineData(10, 0, false)]  // 14:00
    public void Quiet_Hours_Are_22_To_09_In_Yerevan(int utcHour, int utcMinute, bool quiet)
    {
        var at = new DateTimeOffset(2026, 11, 7, utcHour, utcMinute, 0, TimeSpan.Zero);

        Assert.Equal(quiet, ChatUnreadEmailDispatcher.IsQuietHours(at));
    }

    [Fact]
    public async Task Nothing_Is_Sent_Or_Claimed_During_Quiet_Hours_And_It_Goes_Out_At_09_00()
    {
        using var h = new Harness();
        await h.SeedConversationAsync();
        h.Clock.Set(new DateTimeOffset(2026, 11, 7, 18, 30, 0, TimeSpan.Zero)); // 22:30 Yerevan
        await h.AddLineAsync(OwnerId, TimeSpan.FromHours(1));

        Assert.Equal(0, await h.Dispatcher.RunOnceAsync());
        Assert.Null((await h.ParticipantAsync(RenterId)).ChatEmailSentAt);

        h.Clock.Set(new DateTimeOffset(2026, 11, 8, 4, 59, 0, TimeSpan.Zero)); // 08:59
        Assert.Equal(0, await h.Dispatcher.RunOnceAsync());

        h.Clock.Set(new DateTimeOffset(2026, 11, 8, 5, 0, 0, TimeSpan.Zero)); // 09:00
        Assert.Equal(1, await h.Dispatcher.RunOnceAsync());
    }

    // ---- content -------------------------------------------------------------------------------

    [Fact]
    public async Task The_Email_Names_The_Human_Sender_The_Toy_And_Carries_Working_Links_But_No_Text()
    {
        using var h = new Harness();
        await h.SeedConversationAsync();
        var message = await h.AddLineAsync(OwnerId, Old);

        await h.Dispatcher.RunOnceAsync();

        var email = Assert.Single(h.Email.ChatUnreadSent);
        Assert.True(email.HasHumanMessage);
        Assert.Equal("Olga", email.SenderFirstName);
        Assert.False(email.SenderDeleted);
        Assert.Equal("LEGO Duplo Starter Set", email.ToyTitle);
        Assert.Equal("ru", email.PreferredLanguage);
        Assert.Equal("https://dorent.test/images/listings/toy.jpg", email.ToyImageUrl);
        Assert.Equal($"https://dorent.test/chat/{ConversationId}", email.OpenChatUrl);
        Assert.Equal("https://dorent.test/profile/settings", email.SettingsUrl);

        const string prefix = "https://dorent.test/unsubscribe/chat-emails?t=";
        Assert.StartsWith(prefix, email.UnsubscribeUrl);
        Assert.Equal(RenterId, h.Tokens.Validate(email.UnsubscribeUrl[prefix.Length..]));

        var rendered = ChatUnreadEmailTemplate.Render(email);
        Assert.DoesNotContain(message.Body!, rendered.HtmlBody + rendered.TextBody);
    }

    [Fact]
    public async Task A_Deleted_Human_Sender_Is_Reported_As_Deleted()
    {
        using var h = new Harness();
        await h.SeedConversationAsync((owner, _) => owner.DeletedAt = DateTime.UtcNow);
        await h.AddLineAsync(OwnerId, Old);

        await h.Dispatcher.RunOnceAsync();

        var email = Assert.Single(h.Email.ChatUnreadSent);
        Assert.True(email.HasHumanMessage);
        Assert.True(email.SenderDeleted);
        Assert.Null(email.SenderFirstName);
    }

    [Fact]
    public async Task A_Human_Message_Among_System_Lines_Gives_The_Human_Heading()
    {
        using var h = new Harness();
        await h.SeedConversationAsync();
        await h.AddLineAsync(null, TimeSpan.FromMinutes(30), MessageType.System);
        await h.AddLineAsync(OwnerId, TimeSpan.FromMinutes(20));

        await h.Dispatcher.RunOnceAsync();

        var email = h.Email.ChatUnreadSent.Single(sent => sent.To == "renter@test.local");
        Assert.True(email.HasHumanMessage);
        Assert.Equal("Olga", email.SenderFirstName);
    }

    [Theory]
    [InlineData("en", "Olga wrote to you about Duplo")]
    [InlineData("ru", "Сообщение от Olga об игрушке «Duplo»")]
    [InlineData("hy", "«Duplo» խաղալիքի մասին Ձեզ գրել է Olga")]
    [InlineData(null, "Olga wrote to you about Duplo")]
    [InlineData("fr", "Olga wrote to you about Duplo")]
    public void Heading_For_A_Human_Message_Is_Localised_With_An_English_Fallback(string? language, string expected)
    {
        var email = Email(language, hasHuman: true, firstName: "Olga");

        Assert.Equal(expected, ChatUnreadEmailTemplate.Heading(email));
    }

    [Theory]
    [InlineData("en", "New in your chat about Duplo")]
    [InlineData("ru", "Новое в вашем чате об игрушке «Duplo»")]
    [InlineData("hy", "Նոր բան Ձեր զրույցում՝ «Duplo» խաղալիքի մասին")]
    public void Heading_Without_A_Human_Message_Is_Neutral(string language, string expected)
    {
        var email = Email(language, hasHuman: false, firstName: null);

        Assert.Equal(expected, ChatUnreadEmailTemplate.Heading(email));
    }

    [Theory]
    [InlineData("en", "Deleted user wrote to you about Duplo")]
    [InlineData("ru", "Сообщение от Удалённый пользователь об игрушке «Duplo»")]
    [InlineData("hy", "«Duplo» խաղալիքի մասին Ձեզ գրել է Ջնջված օգտատեր")]
    public void Heading_For_A_Deleted_Sender_Uses_The_Localised_Deleted_User(string language, string expected)
    {
        var email = Email(language, hasHuman: true, firstName: null, deleted: true);

        Assert.Equal(expected, ChatUnreadEmailTemplate.Heading(email));
    }

    [Fact]
    public void Rendered_Email_Has_Html_And_Text_Parts_With_The_Three_Links_And_An_Escaped_Title()
    {
        var email = Email("en", hasHuman: true, firstName: "Olga", toyTitle: "<b>Duplo</b> & co");

        var message = ChatUnreadEmailTemplate.Render(email);

        Assert.Equal("a@b.c", message.To);
        Assert.Contains("&lt;b&gt;Duplo&lt;/b&gt; &amp; co", message.HtmlBody);
        Assert.DoesNotContain("<b>Duplo</b>", message.HtmlBody);
        Assert.Contains("background:#C24600", message.HtmlBody);
        Assert.Contains("width=\"64\"", message.HtmlBody);
        foreach (var link in new[] { email.OpenChatUrl, email.UnsubscribeUrl, email.SettingsUrl })
        {
            Assert.Contains(link, message.HtmlBody);
            Assert.Contains(link, message.TextBody);
        }

        Assert.Contains("Unsubscribe from message emails", message.HtmlBody);
        Assert.Contains("Manage in Settings", message.TextBody);
    }

    private static ChatUnreadEmail Email(
        string? language, bool hasHuman, string? firstName, bool deleted = false, string toyTitle = "Duplo") =>
        new(
            To: "a@b.c",
            PreferredLanguage: language,
            ToyTitle: toyTitle,
            ToyImageUrl: "https://dorent.test/i.jpg",
            HasHumanMessage: hasHuman,
            SenderFirstName: firstName,
            SenderDeleted: deleted,
            OpenChatUrl: "https://dorent.test/chat/1",
            UnsubscribeUrl: "https://dorent.test/unsubscribe/chat-emails?t=abc",
            SettingsUrl: "https://dorent.test/profile/settings");

    private sealed class ThrowingChatEmailService : IEmailService
    {
        public int Attempts { get; private set; }

        public Task SendChatUnreadAsync(ChatUnreadEmail email, CancellationToken cancellationToken = default)
        {
            Attempts++;
            throw new InvalidOperationException("Simulated send failure.");
        }

        public Task SendListingApprovedAsync(string ownerEmail, string ownerName, string listingTitle, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SendListingRejectedAsync(string ownerEmail, string ownerName, string listingTitle, string rejectionReason, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SendEmailVerificationAsync(string email, string? preferredLanguage, string link, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SendPasswordResetAsync(string email, string? preferredLanguage, string link, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SendAccountDeletedAsync(string email, string? preferredLanguage, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
