using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Infrastructure.Persistence;
using RentalPlatform.Tests.Push;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Api;

// ADR-067 through the REAL pipeline: routing, [Authorize] with the real JWT validation, model binding of the
// JSON body on DELETE, the ProblemDetails wire shape, the real store, and the real queue + hosted dispatcher
// (only the push client is replaced by FakePushSender).
[Collection("Integration")]
public sealed class PushHttpTests
{
    private static int _ipCounter = 10;

    private readonly RentalPlatformWebAppFactory _factory;

    public PushHttpTests(RentalPlatformWebAppFactory factory) => _factory = factory;

    private static string NextIp() => $"10.78.0.{Interlocked.Increment(ref _ipCounter)}";

    private static HttpClient Authorized(HttpClient client, string? token)
    {
        client.DefaultRequestHeaders.Add(TestRemoteIpStartupFilter.HeaderName, NextIp());
        if (token is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return client;
    }

    private HttpClient Client(string? token = null) => Authorized(_factory.CreateClient(), token);

    private async Task<(Guid Id, string Token)> SeedUserAsync(bool blocked = false, string language = "en", string? firstName = null)
    {
        var id = Guid.NewGuid();
        var email = $"{id:N}@push.local";
        await _factory.SeedAsync(TestData.User(id, email, isBlocked: blocked, firstName: firstName, preferredLanguage: language));
        return (id, TestJwtTokenHelper.GenerateToken(id, email));
    }

    private static object Body(string endpoint) => new { endpoint, p256dh = PushTestKeys.P256dh(), auth = PushTestKeys.Auth() };

    private static Task<HttpResponseMessage> DeleteWithBodyAsync(HttpClient client, string endpoint) =>
        client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/push/subscription")
        {
            Content = JsonContent.Create(new { endpoint })
        });

    private async Task<List<RentalPlatform.Domain.Entities.PushSubscription>> RowsAsync(Guid userId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .PushSubscriptions.AsNoTracking().Where(row => row.UserId == userId).ToListAsync();
    }

    [Fact]
    public async Task The_Public_Key_Is_Anonymous_And_Returned_When_Configured()
    {
        var response = await Client().GetAsync("/api/push/public-key");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("xunit-vapid-public-key", json.GetProperty("publicKey").GetString());
    }

    [Fact]
    public async Task The_Public_Key_Is_404_When_Push_Is_Not_Configured()
    {
        using var off = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IWebPushSettings>();
            services.AddSingleton<IWebPushSettings>(new UnconfiguredSettings());
        }));

        var response = await Authorized(off.CreateClient(), null).GetAsync("/api/push/public-key");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private sealed class UnconfiguredSettings : IWebPushSettings
    {
        public bool IsEnabled => false;
        public string? PublicKey => null;
    }

    [Fact]
    public async Task Put_Subscription_Returns_204_And_Stores_The_Row()
    {
        var (id, token) = await SeedUserAsync();
        var endpoint = PushTestKeys.FcmEndpoint();

        var response = await Client(token).PutAsJsonAsync("/api/push/subscription", Body(endpoint));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var row = Assert.Single(await RowsAsync(id));
        Assert.Equal(endpoint, row.Endpoint);
    }

    [Fact]
    public async Task Put_Subscription_With_An_Invalid_Endpoint_Is_400_Push_Invalid_Subscription()
    {
        var (id, token) = await SeedUserAsync();

        var response = await Client(token).PutAsJsonAsync("/api/push/subscription", Body("https://evil.com/fcm.googleapis.com"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("push.invalid_subscription", json.GetProperty("errorCode").GetString());
        Assert.Empty(await RowsAsync(id));
    }

    [Fact]
    public async Task Put_Subscription_With_Missing_Fields_Is_Also_400_Push_Invalid_Subscription()
    {
        var (_, token) = await SeedUserAsync();

        var response = await Client(token).PutAsJsonAsync("/api/push/subscription", new { endpoint = PushTestKeys.FcmEndpoint() });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("push.invalid_subscription", json.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task Put_Subscription_Without_A_Token_Is_401()
    {
        var response = await Client().PutAsJsonAsync("/api/push/subscription", Body(PushTestKeys.FcmEndpoint()));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Put_Subscription_By_A_Blocked_User_Is_403_And_Stores_Nothing()
    {
        var (id, token) = await SeedUserAsync(blocked: true);

        var response = await Client(token).PutAsJsonAsync("/api/push/subscription", Body(PushTestKeys.FcmEndpoint()));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await RowsAsync(id));
    }

    [Fact]
    public async Task Delete_Subscription_With_A_Json_Body_Removes_The_Row_And_Is_Idempotent()
    {
        var (id, token) = await SeedUserAsync();
        var keep = PushTestKeys.FcmEndpoint();
        var remove = PushTestKeys.FcmEndpoint();
        var client = Client(token);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync("/api/push/subscription", Body(keep))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync("/api/push/subscription", Body(remove))).StatusCode);
        Assert.Equal(2, (await RowsAsync(id)).Count);

        var first = await DeleteWithBodyAsync(client, remove);
        var second = await DeleteWithBodyAsync(client, remove);

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
        // The body was really bound: exactly the named row is gone, the other one stays.
        var left = Assert.Single(await RowsAsync(id));
        Assert.Equal(keep, left.Endpoint);
    }

    [Fact]
    public async Task Delete_Subscription_Cannot_Remove_Another_Users_Row()
    {
        var (ownerId, ownerToken) = await SeedUserAsync();
        var (_, otherToken) = await SeedUserAsync();
        var endpoint = PushTestKeys.FcmEndpoint();
        await Client(ownerToken).PutAsJsonAsync("/api/push/subscription", Body(endpoint));

        var response = await DeleteWithBodyAsync(Client(otherToken), endpoint);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Single(await RowsAsync(ownerId));
    }

    [Fact]
    public async Task Delete_Subscription_Without_A_Token_Is_401()
    {
        var response = await DeleteWithBodyAsync(Client(), PushTestKeys.FcmEndpoint());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Deleting_The_Account_Removes_The_Push_Subscriptions()
    {
        var id = Guid.NewGuid();
        var email = $"{id:N}@push.local";
        await _factory.SeedAsync(TestData.User(id, email, passwordHash: BCrypt.Net.BCrypt.HashPassword("Secret1234")));
        var client = Client(TestJwtTokenHelper.GenerateToken(id, email));
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync("/api/push/subscription", Body(PushTestKeys.FcmEndpoint()))).StatusCode);
        Assert.Single(await RowsAsync(id));

        var request = new HttpRequestMessage(HttpMethod.Delete, "/api/auth/me") { Content = JsonContent.Create(new { password = "Secret1234" }) };
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(request)).StatusCode);

        Assert.Empty(await RowsAsync(id));
    }

    // The whole wiring: ChatService -> IChatPushNotifier -> queue -> hosted dispatcher -> IPushSender.
    [Fact]
    public async Task Sending_A_Chat_Message_Pushes_To_The_Other_Party_Only_And_Without_The_Text()
    {
        var (ownerId, ownerToken) = await SeedUserAsync(firstName: "Olga");
        var (renterId, renterToken) = await SeedUserAsync(language: "ru", firstName: "Ruben");
        var categoryId = Guid.NewGuid();
        var listingId = Guid.NewGuid();
        var bookingId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        await _factory.SeedAsync(TestData.Category(categoryId));
        await _factory.SeedAsync(TestData.Listing(listingId, ownerId, categoryId));
        await _factory.SeedAsync(TestData.Booking(
            bookingId, listingId, renterId, TestData.Today.AddDays(3), TestData.Today.AddDays(5), BookingStatus.Approved));
        var conversation = TestData.Conversation(conversationId, bookingId, ownerId, renterId);
        await _factory.SeedAsync(
            conversation,
            new ConversationParticipant { Id = Guid.NewGuid(), ConversationId = conversationId, UserId = ownerId },
            new ConversationParticipant { Id = Guid.NewGuid(), ConversationId = conversationId, UserId = renterId });

        var ownerEndpoint = PushTestKeys.FcmEndpoint();
        var renterEndpoint = PushTestKeys.FcmEndpoint();
        Assert.Equal(HttpStatusCode.NoContent, (await Client(ownerToken).PutAsJsonAsync("/api/push/subscription", Body(ownerEndpoint))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Client(renterToken).PutAsJsonAsync("/api/push/subscription", Body(renterEndpoint))).StatusCode);

        const string secretText = "my-secret-message-text-4711";
        var send = await Client(ownerToken).PostAsJsonAsync("/api/chat/messages", new { conversationId, content = secretText });
        Assert.Equal(HttpStatusCode.OK, send.StatusCode);

        var delivery = await WaitForDeliveryAsync(renterEndpoint);

        Assert.DoesNotContain(secretText, delivery.PayloadJson);
        Assert.Equal(32, delivery.Topic.Length);
        Assert.Equal(conversationId.ToString("N"), delivery.Topic);
        var payload = JsonDocument.Parse(delivery.PayloadJson).RootElement;
        Assert.Equal("Olga", payload.GetProperty("title").GetString());
        Assert.Contains("игрушке", payload.GetProperty("body").GetString());
        Assert.Equal($"/chat/{conversationId}", payload.GetProperty("url").GetString());
        // The sender never gets a push for their own message.
        await Task.Delay(300);
        Assert.DoesNotContain(_factory.PushSender.Deliveries, item => item.Endpoint == ownerEndpoint);
    }

    private async Task<PushDelivery> WaitForDeliveryAsync(string endpoint)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var found = _factory.PushSender.Deliveries.FirstOrDefault(item => item.Endpoint == endpoint);
            if (found is not null)
            {
                return found;
            }

            await Task.Delay(50);
        }

        throw new Xunit.Sdk.XunitException("The dispatcher did not deliver a push within 5 seconds.");
    }
}
