using Microsoft.Extensions.Options;
using RentalPlatform.Application.DTOs;
using RentalPlatform.Application.Services;
using RentalPlatform.Infrastructure.Services;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Chat;

// ADR-059 amendment: the stateless unsubscribe token and the service behind the page's button.
public sealed class ChatEmailUnsubscribeTests
{
    private const string Secret = "xunit-unsubscribe-test-signing-key-0123456789";
    private static readonly Guid UserId = new("f1000000-0000-0000-0000-000000000001");

    private static ChatEmailUnsubscribeTokens Tokens(FakeTimeProvider clock, string secret = Secret) =>
        new(Options.Create(new JwtOptions { SecretKey = secret }), clock);

    // ---- token ---------------------------------------------------------------------------------

    [Fact]
    public void A_Token_Round_Trips_To_The_Same_User_And_Is_Url_Safe()
    {
        var tokens = Tokens(new FakeTimeProvider());

        var token = tokens.Issue(UserId);

        Assert.Equal(UserId, tokens.Validate(token));
        Assert.Matches("^[A-Za-z0-9_-]+$", token);
    }

    [Fact]
    public void A_Token_Is_Valid_For_60_Days_And_Not_After()
    {
        var clock = new FakeTimeProvider();
        var tokens = Tokens(clock);
        var token = tokens.Issue(UserId);

        clock.Advance(TimeSpan.FromDays(60) - TimeSpan.FromSeconds(1));
        Assert.Equal(UserId, tokens.Validate(token));

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(tokens.Validate(token));
    }

    [Fact]
    public void A_Tampered_Token_Is_Invalid_At_Every_Position()
    {
        var tokens = Tokens(new FakeTimeProvider());
        var token = tokens.Issue(UserId);

        for (var i = 0; i < token.Length; i++)
        {
            var replacement = token[i] == 'A' ? 'B' : 'A';
            var tampered = token[..i] + replacement + token[(i + 1)..];
            Assert.Null(tokens.Validate(tampered));
        }
    }

    [Fact]
    public void A_Token_Signed_With_Another_Secret_Or_Of_Another_User_Is_Invalid()
    {
        var clock = new FakeTimeProvider();
        var foreign = Tokens(clock, "another-signing-key-another-signing-key-1234").Issue(UserId);

        Assert.Null(Tokens(clock).Validate(foreign));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a token")]
    [InlineData("abc")]
    [InlineData("AAAA")]
    public void Garbage_Is_Invalid(string? token)
    {
        Assert.Null(Tokens(new FakeTimeProvider()).Validate(token));
    }

    // ---- service -------------------------------------------------------------------------------

    private static (ChatEmailUnsubscribeService Service, FakeUserAuthStore Store, ChatEmailUnsubscribeTokens Tokens, RentalPlatform.Domain.Entities.User User)
        Build(Action<RentalPlatform.Domain.Entities.User>? configure = null)
    {
        var user = TestData.User(UserId, "u@test.local");
        configure?.Invoke(user);
        var store = new FakeUserAuthStore().Seed(user);
        var tokens = Tokens(new FakeTimeProvider());
        return (new ChatEmailUnsubscribeService(tokens, store), store, tokens, user);
    }

    [Fact]
    public async Task Unsubscribe_Turns_The_Preference_Off_Then_Reports_Already_Off()
    {
        var (service, store, tokens, user) = Build();
        var token = tokens.Issue(UserId);

        var first = await service.UnsubscribeAsync(token);
        Assert.True(first.IsSuccess);
        Assert.Equal(ChatEmailUnsubscribeResponse.Unsubscribed, first.Value!.Result);
        Assert.False(user.ChatEmailsEnabled);
        Assert.Equal(1, store.SaveChangesCallCount);

        var second = await service.UnsubscribeAsync(token);
        Assert.True(second.IsSuccess);
        Assert.Equal(ChatEmailUnsubscribeResponse.AlreadyOff, second.Value!.Result);
        Assert.Equal(1, store.SaveChangesCallCount);
    }

    [Fact]
    public async Task Unsubscribe_Works_For_A_Blocked_User()
    {
        var (service, _, tokens, user) = Build(u => u.IsBlocked = true);

        var result = await service.UnsubscribeAsync(tokens.Issue(UserId));

        Assert.True(result.IsSuccess);
        Assert.False(user.ChatEmailsEnabled);
    }

    [Fact]
    public async Task A_Bad_Token_Is_Invalid_And_Changes_Nothing()
    {
        var (service, store, tokens, user) = Build();
        var token = tokens.Issue(UserId);
        var tampered = token[..^2] + (token[^2] == 'A' ? 'B' : 'A') + token[^1];

        foreach (var bad in new[] { null, "", tampered })
        {
            var result = await service.UnsubscribeAsync(bad);
            Assert.False(result.IsSuccess);
            Assert.Equal("chat_email.unsubscribe_invalid", result.Error!.Code);
        }

        Assert.True(user.ChatEmailsEnabled);
        Assert.Equal(0, store.SaveChangesCallCount);
    }

    [Fact]
    public async Task An_Unknown_Or_Deleted_User_Is_Invalid()
    {
        var (service, _, tokens, user) = Build(u => u.DeletedAt = DateTime.UtcNow);

        var deleted = await service.UnsubscribeAsync(tokens.Issue(UserId));
        var unknown = await service.UnsubscribeAsync(tokens.Issue(Guid.NewGuid()));

        Assert.Equal("chat_email.unsubscribe_invalid", deleted.Error!.Code);
        Assert.Equal("chat_email.unsubscribe_invalid", unknown.Error!.Code);
        Assert.True(user.ChatEmailsEnabled);
    }
}
