using Microsoft.Extensions.Logging.Abstractions;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Infrastructure.Persistence;
using RentalPlatform.Infrastructure.Services;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Infrastructure;

// The server-rendered strings of ADR-056: the notification the renter gets when a deleted owner's request is
// closed (actor "Deleted user" in the RECIPIENT's language, ADR-025) and the confirmation email (en/hy/ru).
public sealed class AccountDeletionCopyTests
{
    private static readonly Guid OwnerId = new("e7000000-0000-0000-0000-000000000001");
    private static readonly Guid RenterId = new("e7000000-0000-0000-0000-000000000002");
    private static readonly Guid CategoryId = new("e7000000-0000-0000-0000-000000000003");
    private static readonly Guid ListingId = new("e7000000-0000-0000-0000-000000000004");

    [Theory]
    [InlineData("en", "Deleted user")]
    [InlineData("ru", "Удалённый пользователь")]
    [InlineData("hy", "Ջնջված օգտատեր")]
    [InlineData(null, "Deleted user")]
    [InlineData("fr", "Deleted user")]
    public async Task The_Renter_Is_Told_By_A_Deleted_User_Card_In_Their_Own_Language(string? language, string expectedActor)
    {
        using var db = new SqliteTestDatabase();
        await db.SeedAsync(TestData.User(RenterId, "renter@test.local", preferredLanguage: language));

        var renter = TestData.User(RenterId, "renter@test.local", preferredLanguage: language);
        var owner = TestData.User(OwnerId, "owner@test.local", firstName: "Gone");
        var listing = TestData.Listing(ListingId, OwnerId, CategoryId);
        listing.Owner = owner;
        var booking = TestData.Booking(Guid.NewGuid(), ListingId, RenterId, TestData.Today.AddDays(5), TestData.Today.AddDays(7), BookingStatus.Rejected);
        booking.Listing = listing;
        booking.Renter = renter;

        await using var context = db.CreateContext();
        var emitter = new NotificationEmitter(new NotificationsStore(context), NullLogger<NotificationEmitter>.Instance);

        await emitter.BookingDeclinedByDeletedOwnerAsync(booking);

        var notification = Assert.Single(context.Notifications);
        Assert.Equal(RenterId, notification.RecipientId);
        Assert.Equal(NotificationKind.Declined, notification.Kind);
        Assert.Equal(expectedActor, notification.ActorName);
        Assert.False(notification.ActorIsSystem);
        Assert.Null(notification.ActorAvatarUrl);
        Assert.DoesNotContain("Gone", notification.Title + notification.Body, StringComparison.Ordinal);
        Assert.Equal(booking.Id, notification.EntityId);
        Assert.Equal(NotificationEntityType.Booking, notification.EntityType);
        if (language is null or "en" or "fr")
        {
            Assert.Equal("Deleted user declined your request", notification.Title);
        }
    }

    [Theory]
    [InlineData("en", "Your DoRent account was deleted")]
    [InlineData("ru", "Ваш аккаунт DoRent удалён")]
    [InlineData("hy", "Ձեր DoRent հաշիվը ջնջվել է")]
    [InlineData("ru-RU", "Ваш аккаунт DoRent удалён")]
    [InlineData(null, "Your DoRent account was deleted")]
    [InlineData("de", "Your DoRent account was deleted")]
    public void The_Confirmation_Email_Is_Localised_With_An_English_Fallback_And_Carries_No_Link_Or_Name(
        string? language, string expectedSubject)
    {
        var message = AccountDeletedEmailTemplate.Render("gone@test.local", language);

        Assert.Equal("gone@test.local", message.To);
        Assert.Equal(expectedSubject, message.Subject);
        Assert.Contains("support@dorent.am", message.HtmlBody);
        Assert.Contains("support@dorent.am", message.TextBody);
        Assert.DoesNotContain("http", message.HtmlBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<a ", message.HtmlBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("gone@test.local", message.HtmlBody + message.TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_Real_Email_Service_Sends_The_Rendered_Message_And_Swallows_A_Failing_Transport()
    {
        var capture = new CaptureEmailSender();
        var service = new EmailService(capture, NullLogger<EmailService>.Instance);

        await service.SendAccountDeletedAsync("gone@test.local", "hy");

        var sent = Assert.Single(capture.Messages);
        Assert.Equal("gone@test.local", sent.To);
        Assert.Equal("Ձեր DoRent հաշիվը ջնջվել է", sent.Subject);

        var failing = new EmailService(new ThrowingSender(), NullLogger<EmailService>.Instance);
        await failing.SendAccountDeletedAsync("gone@test.local", "en"); // must not throw
    }

    private sealed class ThrowingSender : RentalPlatform.Application.Abstractions.IEmailSender
    {
        public Task SendAsync(RentalPlatform.Application.Abstractions.EmailMessage message, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("transport down");
    }
}
