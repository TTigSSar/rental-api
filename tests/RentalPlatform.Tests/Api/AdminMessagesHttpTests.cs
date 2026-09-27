using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Api;

// HTTP contract tests for GET /api/admin/messages/threads. A service-layer test
// (AdminMessagesServiceTests) already proved the filtering logic itself is correct, but it calls
// AdminMessagesService.GetThreadsAsync directly with a hand-built AdminMessageThreadFilter,
// bypassing ASP.NET Core query-string model binding entirely. That gap let a real bug through:
// AdminMessagesController.GetThreads declared the action parameter as
// "[FromQuery] AdminMessageThreadFilter filter" — the parameter name "filter" collides with the
// DTO's own "Filter" property, which defeats ComplexObjectModelBinder's top-level-fallback
// property binding for that one property (Search/Page/PageSize, which don't share a name with the
// parameter, bound correctly). The fix renamed the action parameter (not the DTO property, not the
// "?filter=" wire value) — these tests exercise the real query string so this class of bug can't
// silently regress again.
//
// The moderation thread queue is a single mailbox shared across all admins (one thread per member,
// not per moderator — see IConversationsStore.GetOrCreateForModerationAsync's doc comment), and
// this test class's underlying database is shared across every test in the "Integration" collection
// (RentalPlatformWebAppFactory). Every test therefore seeds its 3 threads with a unique per-test
// token baked into each member's name/email, and every request includes "&search={token}" so a
// test only ever sees its own threads, regardless of what other tests left behind.
[Collection("Integration")]
public sealed class AdminMessagesHttpTests
{
    private readonly RentalPlatformWebAppFactory _factory;

    public AdminMessagesHttpTests(RentalPlatformWebAppFactory factory) => _factory = factory;

    private HttpClient AdminClient(Guid adminId, string email)
    {
        var token = TestJwtTokenHelper.GenerateToken(adminId, email, UserRole.Admin);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    // Three moderation threads, same shape as
    // AdminMessagesServiceTests.GetThreads_Counts_Are_Filter_Independent_And_Match_Each_Filters_Rows:
    //   - needsReplyMemberId: member sent last, unread by the admin -> counts toward Unread AND NeedsReply.
    //   - answeredMemberId: member sent, admin replied, admin marked read -> counts toward neither.
    //   - unreadFromAdminMemberId: member sent, admin replied (so NOT NeedsReply), admin never
    //     marked read -> counts toward Unread only.
    // Overall, within this test's own scope (search={token}): All=3, Unread=2, NeedsReply=1.
    // Every member's first name embeds {token} so "&search={token}" (Contains match) scopes every
    // query in the test down to exactly these 3 threads.
    private async Task<(Guid adminId, string token, Guid needsReplyMemberId, Guid answeredMemberId, Guid unreadFromAdminMemberId)> SeedThreadsAsync(
        string testName)
    {
        var token = $"{testName}-{Guid.NewGuid():N}";
        var adminId = Guid.NewGuid();
        var needsReplyMemberId = Guid.NewGuid();
        var answeredMemberId = Guid.NewGuid();
        var unreadFromAdminMemberId = Guid.NewGuid();

        await _factory.SeedAsync(
            TestData.User(adminId, $"admin-{token}@test.local", role: UserRole.Admin, isIdConfirmed: true),
            TestData.User(needsReplyMemberId, $"needs-reply-{token}@test.local", firstName: $"NeedsReply-{token}"),
            TestData.User(answeredMemberId, $"answered-{token}@test.local", firstName: $"Answered-{token}"),
            TestData.User(unreadFromAdminMemberId, $"unread-from-admin-{token}@test.local", firstName: $"UnreadFromAdmin-{token}"));

        await using var scope = _factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IConversationsStore>();

        var needsReplyConversation = await store.GetOrCreateForModerationAsync(adminId, needsReplyMemberId);
        await store.AddTextMessageAsync(needsReplyConversation.Id, needsReplyMemberId, "Please help");

        var answeredConversation = await store.GetOrCreateForModerationAsync(adminId, answeredMemberId);
        await store.AddTextMessageAsync(answeredConversation.Id, answeredMemberId, "Please help");
        await store.AddTextMessageAsync(answeredConversation.Id, adminId, "We're looking into it");
        await store.MarkReadAsync(answeredConversation.Id, adminId);

        var unreadFromAdminConversation = await store.GetOrCreateForModerationAsync(adminId, unreadFromAdminMemberId);
        await store.AddTextMessageAsync(unreadFromAdminConversation.Id, unreadFromAdminMemberId, "Please help");
        await store.AddTextMessageAsync(unreadFromAdminConversation.Id, adminId, "We're looking into it");

        return (adminId, token, needsReplyMemberId, answeredMemberId, unreadFromAdminMemberId);
    }

    private static async Task<JsonElement> GetJsonAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    private static Guid[] MemberIds(JsonElement root) =>
        root.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("memberId").GetGuid())
            .ToArray();

    [Fact]
    public async Task Filter_Unread_Returns_Only_Unread_Threads()
    {
        var (adminId, token, needsReplyMemberId, answeredMemberId, unreadFromAdminMemberId) =
            await SeedThreadsAsync(nameof(Filter_Unread_Returns_Only_Unread_Threads));
        var client = AdminClient(adminId, $"admin-{token}@test.local");

        var root = await GetJsonAsync(await client.GetAsync($"/api/admin/messages/threads?filter=unread&search={token}"));

        var memberIds = MemberIds(root);
        Assert.Equal(2, memberIds.Length);
        Assert.Contains(needsReplyMemberId, memberIds);
        Assert.Contains(unreadFromAdminMemberId, memberIds);
        Assert.DoesNotContain(answeredMemberId, memberIds);
    }

    [Fact]
    public async Task Filter_NeedsReply_Returns_Only_Needs_Reply_Threads()
    {
        var (adminId, token, needsReplyMemberId, answeredMemberId, unreadFromAdminMemberId) =
            await SeedThreadsAsync(nameof(Filter_NeedsReply_Returns_Only_Needs_Reply_Threads));
        var client = AdminClient(adminId, $"admin-{token}@test.local");

        var root = await GetJsonAsync(await client.GetAsync($"/api/admin/messages/threads?filter=needsReply&search={token}"));

        var items = root.GetProperty("items").EnumerateArray().ToArray();
        var item = Assert.Single(items);
        Assert.Equal(needsReplyMemberId, item.GetProperty("memberId").GetGuid());
        Assert.True(item.GetProperty("needsReply").GetBoolean());
        var memberIds = MemberIds(root);
        Assert.DoesNotContain(answeredMemberId, memberIds);
        Assert.DoesNotContain(unreadFromAdminMemberId, memberIds);
    }

    [Theory]
    [InlineData("filter=all")]
    [InlineData("")]
    [InlineData("filter=zzzznotreal")]
    public async Task Filter_All_Absent_Or_Unrecognised_Returns_Every_Thread(string filterFragment)
    {
        var (adminId, token, needsReplyMemberId, answeredMemberId, unreadFromAdminMemberId) =
            await SeedThreadsAsync($"{nameof(Filter_All_Absent_Or_Unrecognised_Returns_Every_Thread)}-{filterFragment.GetHashCode():x}");
        var client = AdminClient(adminId, $"admin-{token}@test.local");

        var query = string.IsNullOrEmpty(filterFragment)
            ? $"?search={token}"
            : $"?{filterFragment}&search={token}";
        var root = await GetJsonAsync(await client.GetAsync($"/api/admin/messages/threads{query}"));

        var memberIds = MemberIds(root);
        Assert.Equal(3, memberIds.Length);
        Assert.Contains(needsReplyMemberId, memberIds);
        Assert.Contains(answeredMemberId, memberIds);
        Assert.Contains(unreadFromAdminMemberId, memberIds);
    }

    [Fact]
    public async Task Counts_Stay_Filter_Independent_While_Items_Do_Not()
    {
        var (adminId, token, _, _, _) = await SeedThreadsAsync(nameof(Counts_Stay_Filter_Independent_While_Items_Do_Not));
        var client = AdminClient(adminId, $"admin-{token}@test.local");

        var all = await GetJsonAsync(await client.GetAsync($"/api/admin/messages/threads?filter=all&search={token}"));
        var unread = await GetJsonAsync(await client.GetAsync($"/api/admin/messages/threads?filter=unread&search={token}"));
        var needsReply = await GetJsonAsync(await client.GetAsync($"/api/admin/messages/threads?filter=needsReply&search={token}"));

        foreach (var root in new[] { all, unread, needsReply })
        {
            var counts = root.GetProperty("counts");
            Assert.Equal(3, counts.GetProperty("all").GetInt32());
            Assert.Equal(2, counts.GetProperty("unread").GetInt32());
            Assert.Equal(1, counts.GetProperty("needsReply").GetInt32());
        }

        Assert.Equal(3, MemberIds(all).Length);
        Assert.Equal(2, MemberIds(unread).Length);
        Assert.Single(MemberIds(needsReply));
    }

    [Fact]
    public async Task Search_Query_Narrows_Items_Without_Affecting_Counts()
    {
        var (adminId, token, needsReplyMemberId, _, _) =
            await SeedThreadsAsync(nameof(Search_Query_Narrows_Items_Without_Affecting_Counts));
        var client = AdminClient(adminId, $"admin-{token}@test.local");

        // "NeedsReply-{token}" is the first name seeded only for needsReplyMemberId; the other two
        // members' first names are "Answered-{token}"/"UnreadFromAdmin-{token}" and don't match it.
        var root = await GetJsonAsync(await client.GetAsync($"/api/admin/messages/threads?search=NeedsReply-{token}"));

        var memberIds = MemberIds(root);
        Assert.Equal(needsReplyMemberId, Assert.Single(memberIds));
        // Search-filtered counts are still computed over the search-matched set (1), not the
        // whole mailbox — see AdminMessageThreadCounts's doc comment.
        var counts = root.GetProperty("counts");
        Assert.Equal(1, counts.GetProperty("all").GetInt32());
    }

    [Fact]
    public async Task PageSize_Limits_Items_And_Reports_Total_Count()
    {
        var (adminId, token, _, _, _) = await SeedThreadsAsync(nameof(PageSize_Limits_Items_And_Reports_Total_Count));
        var client = AdminClient(adminId, $"admin-{token}@test.local");

        var root = await GetJsonAsync(await client.GetAsync($"/api/admin/messages/threads?page=1&pageSize=2&search={token}"));

        Assert.Equal(2, MemberIds(root).Length);
        Assert.Equal(3, root.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, root.GetProperty("totalPages").GetInt32());
        Assert.Equal(1, root.GetProperty("page").GetInt32());
        Assert.Equal(2, root.GetProperty("pageSize").GetInt32());
    }
}
