using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Api;

// P3-3 GET /api/admin/demand through the real pipeline (plan section 4: #10 HTTP, #12). The shared host
// has one database, so each test uses a day range no other test writes to.
[Collection("Integration")]
public sealed class AdminDemandHttpTests
{
    private readonly RentalPlatformWebAppFactory _factory;

    public AdminDemandHttpTests(RentalPlatformWebAppFactory factory) => _factory = factory;

    private HttpClient Client(Guid? userId = null, string? email = null, UserRole role = UserRole.User)
    {
        var client = _factory.CreateClient();
        if (userId is { } id)
        {
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", TestJwtTokenHelper.GenerateToken(id, email!, role));
        }

        return client;
    }

    private async Task<HttpClient> AdminClientAsync()
    {
        var id = Guid.NewGuid();
        var email = $"{id:N}@demand-http.local";
        await _factory.SeedAsync(TestData.User(id, email, role: UserRole.Admin));
        return Client(id, email, UserRole.Admin);
    }

    [Fact]
    public async Task Test12_The_Exact_Json_Shape_Dates_And_Null_District()
    {
        var category = Guid.NewGuid();
        var district = Guid.NewGuid();
        await _factory.SeedAsync(
            new DemandDailyCount { Id = Guid.NewGuid(), Day = new DateOnly(2037, 5, 2), DistrictId = null, CategoryId = category, BookingRequests = 4 },
            new DemandDailyCount { Id = Guid.NewGuid(), Day = new DateOnly(2037, 5, 2), DistrictId = district, CategoryId = category, BookingRequests = 2 });
        var client = await AdminClientAsync();

        var response = await client.GetAsync("/api/admin/demand?from=2037-05-01&to=2037-05-03");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.Equal(new[] { "from", "to", "rows" }, root.EnumerateObject().Select(p => p.Name));
        Assert.Equal("2037-05-01", root.GetProperty("from").GetString());
        Assert.Equal("2037-05-03", root.GetProperty("to").GetString());

        var rows = root.GetProperty("rows").EnumerateArray().ToList();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row =>
            Assert.Equal(new[] { "day", "districtId", "categoryId", "bookingRequests" }, row.EnumerateObject().Select(p => p.Name)));

        // NULL district first, and serialized as null (present), not omitted.
        Assert.Equal(JsonValueKind.Null, rows[0].GetProperty("districtId").ValueKind);
        Assert.Equal(4, rows[0].GetProperty("bookingRequests").GetInt32());
        Assert.Equal("2037-05-02", rows[0].GetProperty("day").GetString());
        Assert.Equal(category, rows[0].GetProperty("categoryId").GetGuid());
        Assert.Equal(district, rows[1].GetProperty("districtId").GetGuid());
    }

    [Fact]
    public async Task An_Empty_Range_Returns_An_Empty_Array()
    {
        var client = await AdminClientAsync();

        var response = await client.GetAsync("/api/admin/demand?from=2038-01-01&to=2038-01-02");

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("rows").ValueKind);
        Assert.Equal(0, doc.RootElement.GetProperty("rows").GetArrayLength());
    }

    [Fact]
    public async Task Without_Parameters_The_Default_Thirty_Day_Range_Is_Echoed()
    {
        var client = await AdminClientAsync();

        var response = await client.GetAsync("/api/admin/demand");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var from = DateOnly.ParseExact(doc.RootElement.GetProperty("from").GetString()!, "yyyy-MM-dd");
        var to = DateOnly.ParseExact(doc.RootElement.GetProperty("to").GetString()!, "yyyy-MM-dd");
        Assert.Equal(29, to.DayNumber - from.DayNumber);
    }

    [Theory]
    [InlineData("from=2037-05-01")]
    [InlineData("to=2037-05-01")]
    [InlineData("from=garbage&to=2037-05-01")]
    [InlineData("from=2037-05-03&to=2037-05-01")]
    [InlineData("from=2036-01-01&to=2037-01-02")] // 368 days inclusive
    public async Task Test10_Invalid_Ranges_Return_400_With_The_Range_Error_Code(string query)
    {
        var client = await AdminClientAsync();

        var response = await client.GetAsync($"/api/admin/demand?{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("admin.demand_range_invalid", doc.RootElement.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task A_Range_Of_Exactly_366_Days_Is_Accepted()
    {
        var client = await AdminClientAsync();

        var response = await client.GetAsync("/api/admin/demand?from=2039-01-01&to=2040-01-01");  // 366 days inclusive; the next day makes 367

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/admin/demand?from=2039-01-01&to=2040-01-02")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Anonymous_Is_401_And_A_Renter_Is_403()
    {
        var anonymous = await Client().GetAsync("/api/admin/demand");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var userId = Guid.NewGuid();
        var email = $"{userId:N}@demand-http-user.local";
        await _factory.SeedAsync(TestData.User(userId, email));
        var forbidden = await Client(userId, email).GetAsync("/api/admin/demand");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }
}
