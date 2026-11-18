using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.DTOs;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Infrastructure.Persistence;
using RentalPlatform.Infrastructure.Services;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Push;

// ADR-067: recipients, payload, the 404/410 clean-up. The REAL dispatcher over the REAL schema on SQLite; only the
// push client is the recording fake.
public sealed class ChatPushDispatcherTests : IDisposable
{
    private static readonly Guid OwnerId = new("e2000000-0000-0000-0000-000000000001");
    private static readonly Guid RenterId = new("e2000000-0000-0000-0000-000000000002");
    private static readonly Guid CategoryId = new("e2000000-0000-0000-0000-000000000003");
    private static readonly Guid ListingId = new("e2000000-0000-0000-0000-000000000004");
    private static readonly Guid BookingId = new("e2000000-0000-0000-0000-000000000005");
    private static readonly Guid ConversationId = new("e2000000-0000-0000-0000-000000000006");

    private readonly SqliteTestDatabase _db = new();
    private readonly FakePushSender _sender = new();
    private readonly ChatPushQueue _queue = new();
    private readonly ChatPushDispatcher _dispatcher;
    private readonly string _ownerEndpoint = PushTestKeys.FcmEndpoint();
    private readonly string _renterEndpoint = PushTestKeys.FcmEndpoint();

    private sealed class Settings : IWebPushSettings
    {
        public bool IsEnabled { get; set; } = true;
        public string? PublicKey => "public-key";
    }

    private readonly Settings _settings = new();

    public ChatPushDispatcherTests()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => _db.CreateContext());
        services.AddSingleton<IPushSender>(_sender);
        var provider = services.BuildServiceProvider();
        _dispatcher = new ChatPushDispatcher(
            provider.GetRequiredService<IServiceScopeFactory>(),
            _queue,
            _settings,
            NullLogger<ChatPushDispatcher>.Instance);
    }

    public void Dispose() => _db.Dispose();

    private async Task SeedAsync(
        ConversationKind kind = ConversationKind.Booking,
        Action<User, User>? configure = null)
    {
        var owner = TestData.User(OwnerId, "owner@pushd.local", firstName: "Olga", preferredLanguage: "en");
        var renter = TestData.User(RenterId, "renter@pushd.local", firstName: "Ruben", preferredLanguage: "ru");
        configure?.Invoke(owner, renter);
        await _db.SeedAsync(owner, renter, TestData.Category(CategoryId));
        await _db.SeedAsync(TestData.Listing(ListingId, OwnerId, CategoryId));
        await _db.SeedAsync(TestData.Booking(
            BookingId, ListingId, RenterId, TestData.Today.AddDays(3), TestData.Today.AddDays(5), BookingStatus.Approved));

        var conversation = TestData.Conversation(ConversationId, BookingId, OwnerId, RenterId);
        conversation.Kind = kind;
        conversation.ToyTitle = "Wooden train";
        await _db.SeedAsync(conversation);
        await _db.SeedAsync(
            Subscription(OwnerId, _ownerEndpoint),
            Subscription(RenterId, _renterEndpoint));
    }

    private static RentalPlatform.Domain.Entities.PushSubscription Subscription(Guid userId, string endpoint) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        Endpoint = endpoint,
        EndpointHash = RentalPlatform.Application.Services.PushSubscriptionService.HashEndpoint(endpoint),
        P256dh = PushTestKeys.P256dh(),
        Auth = PushTestKeys.Auth(),
        CreatedAt = DateTime.UtcNow
    };

    private async Task<int> RowCountAsync()
    {
        await using var context = _db.CreateContext();
        return await context.PushSubscriptions.CountAsync();
    }

    [Fact]
    public async Task The_Recipient_Gets_A_Push_And_The_Sender_Does_Not()
    {
        await SeedAsync();

        var delivered = await _dispatcher.ProcessAsync(new ChatPushJob(ConversationId, OwnerId));

        Assert.Equal(1, delivered);
        var delivery = Assert.Single(_sender.Deliveries);
        Assert.Equal(_renterEndpoint, delivery.Endpoint);
        Assert.Equal(24 * 60 * 60, delivery.TimeToLiveSeconds);
        Assert.Equal(32, delivery.Topic.Length);
        Assert.Equal(ConversationId.ToString("N"), delivery.Topic);
    }

    [Fact]
    public async Task The_Payload_Is_In_The_Recipients_Language_And_Has_No_Message_Text()
    {
        await SeedAsync();
        await _db.SeedAsync(TestData.ChatMessage(Guid.NewGuid(), ConversationId, OwnerId, body: "top-secret-text-9911"));

        await _dispatcher.ProcessAsync(new ChatPushJob(ConversationId, OwnerId));

        var delivery = Assert.Single(_sender.Deliveries);
        Assert.DoesNotContain("top-secret-text-9911", delivery.PayloadJson);
        var payload = JsonDocument.Parse(delivery.PayloadJson).RootElement;
        Assert.Equal("Olga", payload.GetProperty("title").GetString());
        Assert.Equal("Новое сообщение об игрушке «Wooden train»", payload.GetProperty("body").GetString());
        Assert.Equal(ConversationId.ToString("D"), payload.GetProperty("conversationId").GetString());
        Assert.Equal($"/chat/{ConversationId}", payload.GetProperty("url").GetString());
    }

    [Fact]
    public async Task The_Other_Direction_Pushes_To_The_Owner_In_English()
    {
        await SeedAsync();

        await _dispatcher.ProcessAsync(new ChatPushJob(ConversationId, RenterId));

        var delivery = Assert.Single(_sender.Deliveries);
        Assert.Equal(_ownerEndpoint, delivery.Endpoint);
        var payload = JsonDocument.Parse(delivery.PayloadJson).RootElement;
        Assert.Equal("Ruben", payload.GetProperty("title").GetString());
        Assert.Equal("New message about «Wooden train»", payload.GetProperty("body").GetString());
    }

    [Fact]
    public async Task A_Moderation_Thread_Pushes_The_Neutral_Heading_To_The_Member_Only()
    {
        await SeedAsync(ConversationKind.Moderation, (owner, _) => owner.Role = UserRole.Admin);

        // The moderator wrote (OwnerId is the moderator by convention): the member gets it.
        await _dispatcher.ProcessAsync(new ChatPushJob(ConversationId, OwnerId));

        var delivery = Assert.Single(_sender.Deliveries);
        Assert.Equal(_renterEndpoint, delivery.Endpoint);
        var payload = JsonDocument.Parse(delivery.PayloadJson).RootElement;
        Assert.Equal("Модерация DoRent: новое сообщение", payload.GetProperty("body").GetString());

        // The member writing to the moderator pushes nobody (the moderator is Admin).
        await _dispatcher.ProcessAsync(new ChatPushJob(ConversationId, RenterId));
        Assert.Single(_sender.Deliveries);
    }

    [Fact]
    public async Task A_Blocked_Recipient_Is_Skipped()
    {
        await SeedAsync(configure: (_, renter) => renter.IsBlocked = true);
        await _dispatcher.ProcessAsync(new ChatPushJob(ConversationId, OwnerId));
        Assert.Empty(_sender.Deliveries);
    }

    [Fact]
    public async Task A_Deleted_Recipient_Is_Skipped()
    {
        await SeedAsync(configure: (_, renter) => renter.DeletedAt = DateTime.UtcNow);
        await _dispatcher.ProcessAsync(new ChatPushJob(ConversationId, OwnerId));
        Assert.Empty(_sender.Deliveries);
    }

    [Fact]
    public async Task An_Admin_Recipient_Is_Skipped()
    {
        await SeedAsync(configure: (_, renter) => renter.Role = UserRole.Admin);
        await _dispatcher.ProcessAsync(new ChatPushJob(ConversationId, OwnerId));
        Assert.Empty(_sender.Deliveries);
    }

    [Fact]
    public async Task A_410_From_The_Push_Service_Removes_That_Subscription_Only()
    {
        await SeedAsync();
        await using (var context = _db.CreateContext())
        {
            // A second device of the renter that keeps working.
            context.PushSubscriptions.Add(Subscription(RenterId, PushTestKeys.FcmEndpoint()));
            await context.SaveChangesAsync();
        }

        _sender.Outcome = delivery => delivery.Endpoint == _renterEndpoint ? PushSendOutcome.Gone : PushSendOutcome.Sent;

        var delivered = await _dispatcher.ProcessAsync(new ChatPushJob(ConversationId, OwnerId));

        Assert.Equal(1, delivered);
        await using var read = _db.CreateContext();
        Assert.DoesNotContain(await read.PushSubscriptions.ToListAsync(), row => row.Endpoint == _renterEndpoint);
        Assert.Equal(2, await read.PushSubscriptions.CountAsync());
    }

    [Fact]
    public async Task Another_Failure_Keeps_The_Subscription_And_Is_Not_Retried()
    {
        await SeedAsync();
        _sender.Outcome = _ => PushSendOutcome.Failed;

        var delivered = await _dispatcher.ProcessAsync(new ChatPushJob(ConversationId, OwnerId));

        Assert.Equal(0, delivered);
        Assert.Single(_sender.Deliveries);
        Assert.Equal(2, await RowCountAsync());
    }

    [Fact]
    public async Task A_Stored_Endpoint_That_Fails_Validation_Is_Never_Sent_To_And_Is_Removed()
    {
        await SeedAsync();
        await using (var context = _db.CreateContext())
        {
            context.PushSubscriptions.Add(Subscription(RenterId, "https://evil.com/collect"));
            await context.SaveChangesAsync();
        }

        await _dispatcher.ProcessAsync(new ChatPushJob(ConversationId, OwnerId));

        Assert.DoesNotContain(_sender.Deliveries, delivery => delivery.Endpoint.Contains("evil.com"));
        await using var read = _db.CreateContext();
        Assert.DoesNotContain(await read.PushSubscriptions.ToListAsync(), row => row.Endpoint.Contains("evil.com"));
    }

    [Fact]
    public async Task Nothing_Is_Sent_When_Push_Is_Not_Configured()
    {
        await SeedAsync();
        _settings.IsEnabled = false;

        Assert.Equal(0, await _dispatcher.ProcessAsync(new ChatPushJob(ConversationId, OwnerId)));
        Assert.Empty(_sender.Deliveries);
    }

    // --- the notifier: what gets queued ---

    private ChatPushNotifier Notifier() => new(_queue, _settings, NullLogger<ChatPushNotifier>.Instance);

    private static ChatRealtimeMessage Message(string type, Guid? senderId) => new()
    {
        Id = Guid.NewGuid(),
        ConversationId = ConversationId,
        SenderId = senderId,
        Type = type,
        Body = "text",
        SentAt = DateTime.UtcNow
    };

    [Theory]
    [InlineData("text", true)]
    [InlineData("image", true)]
    [InlineData("location", true)]
    [InlineData("moderationNote", true)]
    [InlineData("system", false)]
    public async Task Only_Human_Lines_And_Moderator_Notes_Are_Queued(string type, bool queued)
    {
        var senderId = type == "system" ? (Guid?)null : OwnerId;
        await Notifier().MessageSentAsync(Message(type, senderId), OwnerId, RenterId);

        Assert.Equal(queued, _queue.Reader.TryRead(out var job));
        if (queued)
        {
            Assert.Equal(new ChatPushJob(ConversationId, OwnerId), job);
        }
    }

    [Fact]
    public async Task A_System_Line_Is_Not_Queued_Even_With_A_Sender_Id()
    {
        await Notifier().MessageSentAsync(Message("system", OwnerId), OwnerId, RenterId);

        Assert.False(_queue.Reader.TryRead(out _));
    }

    [Fact]
    public async Task Nothing_Is_Queued_When_Push_Is_Not_Configured()
    {
        _settings.IsEnabled = false;
        await Notifier().MessageSentAsync(Message("text", OwnerId), OwnerId, RenterId);

        Assert.False(_queue.Reader.TryRead(out _));
    }

    [Fact]
    public void The_Queue_Is_Bounded_And_Drops_The_Oldest_Job_Without_Blocking()
    {
        var first = new ChatPushJob(Guid.NewGuid(), OwnerId);
        _queue.TryEnqueue(first);
        for (var index = 0; index < ChatPushQueue.Capacity; index++)
        {
            Assert.True(_queue.TryEnqueue(new ChatPushJob(Guid.NewGuid(), OwnerId)));
        }

        var drained = new List<ChatPushJob>();
        while (_queue.Reader.TryRead(out var job))
        {
            drained.Add(job);
        }

        Assert.Equal(ChatPushQueue.Capacity, drained.Count);
        Assert.DoesNotContain(first, drained);
    }
}
