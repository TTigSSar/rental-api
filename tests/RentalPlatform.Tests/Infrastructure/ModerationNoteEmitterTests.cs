using Microsoft.Extensions.Logging.Abstractions;
using RentalPlatform.Application.Common;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Infrastructure.Services;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Infrastructure;

// ModerationNoteEmitter mirrors ChatSystemMessageEmitter's try/catch-swallow + log doctrine: a
// note failure must never propagate to (and so never fail) the moderation action that triggered
// it. Each of the three emit methods is exercised against a store where every member throws.
// Body/reason are rendered in the member's language at emit time (ADR-025 amendment 2026-10-26).
public sealed class ModerationNoteEmitterTests
{
    private sealed class CapturingStore : ThrowingConversationsStore
    {
        public ModerationNoteKind? Kind { get; private set; }
        public string? Subject { get; private set; }
        public string? Reason { get; private set; }
        public string? Body { get; private set; }

        public override Task<Conversation> GetOrCreateForModerationAsync(Guid moderatorId, Guid memberId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new Conversation { Id = Guid.NewGuid(), OwnerId = moderatorId, RenterId = memberId });

        public override Task<ChatMessage> AddModerationNoteAsync(Guid conversationId, Guid moderatorId, ModerationNoteKind kind, string? subject, string? reason, string body, CancellationToken cancellationToken = default)
        {
            Kind = kind;
            Subject = subject;
            Reason = reason;
            Body = body;
            return Task.FromResult(new ChatMessage { Id = Guid.NewGuid(), CreatedAt = DateTime.UtcNow });
        }
    }

    private static ModerationNoteEmitter CreateEmitter() =>
        new(
            new ThrowingConversationsStore(),
            new FakeChatRealtimeNotifier(),
            NullLogger<ModerationNoteEmitter>.Instance);

    private static (ModerationNoteEmitter Emitter, CapturingStore Store) CreateCapturing()
    {
        var store = new CapturingStore();
        return (new ModerationNoteEmitter(store, new FakeChatRealtimeNotifier(), NullLogger<ModerationNoteEmitter>.Instance), store);
    }

    [Fact]
    public async Task ListingRejectedAsync_Swallows_Store_Failure()
    {
        var emitter = CreateEmitter();

        var exception = await Record.ExceptionAsync(() =>
            emitter.ListingRejectedAsync(Guid.NewGuid(), Guid.NewGuid(), "en", "Some Listing", "other", "Please add photos."));

        Assert.Null(exception);
    }

    [Fact]
    public async Task ListingRecategorisedAsync_Swallows_Store_Failure()
    {
        var emitter = CreateEmitter();

        var exception = await Record.ExceptionAsync(() =>
            emitter.ListingRecategorisedAsync(Guid.NewGuid(), Guid.NewGuid(), "ru", "Some Listing", "Building Blocks", "Outdoor Toys"));

        Assert.Null(exception);
    }

    [Fact]
    public async Task AccountSuspendedAsync_Swallows_Store_Failure()
    {
        var emitter = CreateEmitter();

        var exception = await Record.ExceptionAsync(() =>
            emitter.AccountSuspendedAsync(Guid.NewGuid(), Guid.NewGuid(), "hy", "Repeated policy violations."));

        Assert.Null(exception);
    }

    [Theory]
    [InlineData("en", "Your listing \"Toy\" was rejected: {0}.")]
    [InlineData("ru", "Объявление «Toy» отклонено: {0}.")]
    [InlineData("hy", "«Toy» հայտարարությունը մերժվել է՝ {0}։")]
    [InlineData("ru-RU", "Объявление «Toy» отклонено: {0}.")]
    [InlineData("de", "Your listing \"Toy\" was rejected: {0}.")]
    [InlineData(null, "Your listing \"Toy\" was rejected: {0}.")]
    public async Task ListingRejected_Renders_Body_And_Reason_In_Member_Language(string? language, string template)
    {
        var (emitter, store) = CreateCapturing();

        await emitter.ListingRejectedAsync(Guid.NewGuid(), Guid.NewGuid(), language, "Toy", "unsafeItem", "  ");

        var expectedLabel = RejectionReasonCatalog.LabelFor("unsafeItem", language);
        Assert.Equal(ModerationNoteKind.Reject, store.Kind);
        Assert.Equal("Toy", store.Subject);
        Assert.Equal(expectedLabel, store.Reason);
        Assert.Equal(string.Format(template, expectedLabel), store.Body);
    }

    [Fact]
    public async Task ListingRejected_Reason_Label_Is_Localized()
    {
        var (en, enStore) = CreateCapturing();
        var (ru, ruStore) = CreateCapturing();
        await en.ListingRejectedAsync(Guid.NewGuid(), Guid.NewGuid(), "en", "Toy", "unsafeItem", null);
        await ru.ListingRejectedAsync(Guid.NewGuid(), Guid.NewGuid(), "ru", "Toy", "unsafeItem", null);

        Assert.Equal("Unsafe item", enStore.Reason);
        Assert.NotEqual(enStore.Reason, ruStore.Reason);
    }

    [Fact]
    public async Task ListingRejected_Admin_Note_Stays_As_Written_In_Any_Language()
    {
        var (emitter, store) = CreateCapturing();

        await emitter.ListingRejectedAsync(Guid.NewGuid(), Guid.NewGuid(), "hy", "Toy", "unsafeItem", "  Small parts.  ");

        Assert.Equal("Small parts.", store.Body);
    }

    [Theory]
    [InlineData("en", "Your listing \"Toy\" was moved to a different category: Blocks → Outdoor.")]
    [InlineData("ru", "Объявление «Toy» перенесено в другую категорию: Blocks → Outdoor.")]
    [InlineData("hy", "«Toy» հայտարարությունը տեղափոխվել է այլ կատեգորիա՝ Blocks → Outdoor։")]
    [InlineData("xx", "Your listing \"Toy\" was moved to a different category: Blocks → Outdoor.")]
    [InlineData(null, "Your listing \"Toy\" was moved to a different category: Blocks → Outdoor.")]
    public async Task ListingRecategorised_Renders_Body_In_Member_Language(string? language, string expectedBody)
    {
        var (emitter, store) = CreateCapturing();

        await emitter.ListingRecategorisedAsync(Guid.NewGuid(), Guid.NewGuid(), language, "Toy", "Blocks", "Outdoor");

        Assert.Equal(ModerationNoteKind.Category, store.Kind);
        Assert.Equal("Blocks → Outdoor", store.Reason);
        Assert.Equal(expectedBody, store.Body);
    }

    [Theory]
    [InlineData("en", "Your account has been suspended.")]
    [InlineData("ru", "Ваш аккаунт заблокирован.")]
    [InlineData("hy", "Ձեր հաշիվը կասեցվել է։")]
    [InlineData("xx", "Your account has been suspended.")]
    [InlineData(null, "Your account has been suspended.")]
    public async Task AccountSuspended_Without_Reason_Renders_In_Member_Language(string? language, string expectedBody)
    {
        var (emitter, store) = CreateCapturing();

        await emitter.AccountSuspendedAsync(Guid.NewGuid(), Guid.NewGuid(), language, "  ");

        Assert.Equal(ModerationNoteKind.Suspend, store.Kind);
        Assert.Null(store.Reason);
        Assert.Equal(expectedBody, store.Body);
    }

    [Theory]
    [InlineData("en", "Spam", "Your account has been suspended. Reason: Spam.")]
    [InlineData("ru", "Spam", "Ваш аккаунт заблокирован. Причина: Spam.")]
    [InlineData("hy", "Spam", "Ձեր հաշիվը կասեցվել է։ Պատճառը՝ Spam։")]
    [InlineData(null, "Spam", "Your account has been suspended. Reason: Spam.")]
    [InlineData("en", "Spam!", "Your account has been suspended. Reason: Spam!")]
    [InlineData("ru", "Спам.", "Ваш аккаунт заблокирован. Причина: Спам.")]
    [InlineData("hy", "Սպամ։", "Ձեր հաշիվը կասեցվել է։ Պատճառը՝ Սպամ։")]
    [InlineData("en", "Why?", "Your account has been suspended. Reason: Why?")]
    [InlineData("en", "Etc…", "Your account has been suspended. Reason: Etc…")]
    public async Task AccountSuspended_With_Reason_Renders_And_Does_Not_Double_The_Stop(string? language, string reason, string expectedBody)
    {
        var (emitter, store) = CreateCapturing();

        await emitter.AccountSuspendedAsync(Guid.NewGuid(), Guid.NewGuid(), language, reason);

        Assert.Equal(reason, store.Reason);
        Assert.Equal(expectedBody, store.Body);
    }
}
