using RentalPlatform.Application.Services;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Auth;

// Unit coverage for AuthService.UpdatePreferredLanguageAsync over hand-rolled in-memory
// fakes (no DbContext needed — AuthService talks only through IUserAuthStore).
public sealed class AuthServiceTests
{
    private static readonly Guid UserId = new("b0000000-0000-0000-0000-000000000001");

    private static AuthService CreateService(FakeUserAuthStore store, Guid? currentUserId) =>
        new(
            store,
            new FakePasswordHasher(),
            new FakeJwtTokenService(),
            new FakeCurrentUserContext(currentUserId),
            new FakeExternalIdentityTokenValidator());

    [Fact]
    public async Task UpdatePreferredLanguage_Valid_Code_Is_Normalized_And_Persisted()
    {
        var user = TestData.User(UserId, "user@test.local");
        var store = new FakeUserAuthStore().Seed(user);
        var service = CreateService(store, UserId);

        var result = await service.UpdatePreferredLanguageAsync("HY");

        Assert.True(result.IsSuccess);
        Assert.Equal("hy", result.Value!.PreferredLanguage);
        Assert.Equal("hy", user.PreferredLanguage);
        Assert.Equal(1, store.SaveChangesCallCount);
    }

    [Fact]
    public async Task UpdatePreferredLanguage_Null_Clears_Preference()
    {
        var user = TestData.User(UserId, "user@test.local");
        var store = new FakeUserAuthStore().Seed(user);
        var service = CreateService(store, UserId);

        var result = await service.UpdatePreferredLanguageAsync(null);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value!.PreferredLanguage);
        Assert.Null(user.PreferredLanguage);
        Assert.Equal(1, store.SaveChangesCallCount);
    }

    [Fact]
    public async Task UpdatePreferredLanguage_Empty_String_Clears_Preference()
    {
        var user = TestData.User(UserId, "user@test.local");
        var store = new FakeUserAuthStore().Seed(user);
        var service = CreateService(store, UserId);

        var result = await service.UpdatePreferredLanguageAsync("   ");

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value!.PreferredLanguage);
        Assert.Null(user.PreferredLanguage);
        Assert.Equal(1, store.SaveChangesCallCount);
    }

    [Fact]
    public async Task UpdatePreferredLanguage_Invalid_Code_Fails_Without_Persisting()
    {
        var user = TestData.User(UserId, "user@test.local");
        var store = new FakeUserAuthStore().Seed(user);
        var service = CreateService(store, UserId);

        var result = await service.UpdatePreferredLanguageAsync("fr");

        Assert.False(result.IsSuccess);
        Assert.Equal("auth.invalid_language", result.Error!.Code);
        Assert.Equal("en", user.PreferredLanguage);
        Assert.Equal(0, store.SaveChangesCallCount);
    }

    [Fact]
    public async Task UpdatePreferredLanguage_No_Current_User_Returns_Unauthenticated()
    {
        var store = new FakeUserAuthStore();
        var service = CreateService(store, currentUserId: null);

        var result = await service.UpdatePreferredLanguageAsync("en");

        Assert.False(result.IsSuccess);
        Assert.Equal("auth.unauthenticated", result.Error!.Code);
        Assert.Equal(0, store.SaveChangesCallCount);
    }

    [Fact]
    public async Task UpdatePreferredLanguage_User_Not_Found_Returns_Unauthenticated()
    {
        var store = new FakeUserAuthStore();
        var service = CreateService(store, UserId);

        var result = await service.UpdatePreferredLanguageAsync("en");

        Assert.False(result.IsSuccess);
        Assert.Equal("auth.unauthenticated", result.Error!.Code);
        Assert.Equal(0, store.SaveChangesCallCount);
    }
}
