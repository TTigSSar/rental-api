using RentalPlatform.Application.DTOs;
using RentalPlatform.Application.Services;
using RentalPlatform.Infrastructure.Services;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Auth;

// Unit coverage for AuthService.UpdatePreferredLanguageAsync and ChangePasswordAsync over
// hand-rolled in-memory fakes (no DbContext needed — AuthService talks only through
// IUserAuthStore).
public sealed class AuthServiceTests
{
    private static readonly Guid UserId = new("b0000000-0000-0000-0000-000000000001");

    private static AuthService CreateService(FakeUserAuthStore store, Guid? currentUserId) =>
        AuthServiceFactory.ForFakes(store, currentUserId);

    // Real BCrypt hasher for the tests that need genuine hash behavior (empty-hash guard,
    // end-to-end hash change) — FakePasswordHasher returns false instead of throwing on an
    // empty stored hash, which would hide the bug this covers (see M-013).
    private static AuthService CreateServiceWithRealHasher(FakeUserAuthStore store, Guid? currentUserId) =>
        AuthServiceFactory.ForFakes(store, currentUserId, new BcryptPasswordHasher());

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

    [Fact]
    public async Task ChangePassword_Success_Persists_New_Hash()
    {
        var user = TestData.User(UserId, "user@test.local", passwordHash: "hashed:OldPass1");
        var store = new FakeUserAuthStore().Seed(user);
        var service = CreateService(store, UserId);

        var result = await service.ChangePasswordAsync("OldPass1", "NewPass2");

        Assert.True(result.IsSuccess);
        Assert.True(result.Value);
        Assert.Equal("hashed:NewPass2", user.PasswordHash);
        Assert.Equal(1, store.SaveChangesCallCount);
    }

    [Fact]
    public async Task ChangePassword_No_Current_User_Returns_Unauthenticated()
    {
        var store = new FakeUserAuthStore();
        var service = CreateService(store, currentUserId: null);

        var result = await service.ChangePasswordAsync("OldPass1", "NewPass2");

        Assert.False(result.IsSuccess);
        Assert.Equal("auth.unauthenticated", result.Error!.Code);
        Assert.Equal(0, store.SaveChangesCallCount);
    }

    [Fact]
    public async Task ChangePassword_User_Not_Found_Returns_Unauthenticated()
    {
        var store = new FakeUserAuthStore();
        var service = CreateService(store, UserId);

        var result = await service.ChangePasswordAsync("OldPass1", "NewPass2");

        Assert.False(result.IsSuccess);
        Assert.Equal("auth.unauthenticated", result.Error!.Code);
        Assert.Equal(0, store.SaveChangesCallCount);
    }

    [Fact]
    public async Task ChangePassword_Blocked_User_Returns_UserBlocked()
    {
        var user = TestData.User(UserId, "user@test.local", isBlocked: true, passwordHash: "hashed:OldPass1");
        var store = new FakeUserAuthStore().Seed(user);
        var service = CreateService(store, UserId);

        var result = await service.ChangePasswordAsync("OldPass1", "NewPass2");

        Assert.False(result.IsSuccess);
        Assert.Equal("auth.user_blocked", result.Error!.Code);
        Assert.Equal(0, store.SaveChangesCallCount);
    }

    [Fact]
    public async Task ChangePassword_Wrong_Current_Password_Returns_InvalidCurrentPassword()
    {
        var user = TestData.User(UserId, "user@test.local", passwordHash: "hashed:OldPass1");
        var store = new FakeUserAuthStore().Seed(user);
        var service = CreateService(store, UserId);

        var result = await service.ChangePasswordAsync("WrongPass1", "NewPass2");

        Assert.False(result.IsSuccess);
        Assert.Equal("auth.invalid_current_password", result.Error!.Code);
        Assert.Equal("hashed:OldPass1", user.PasswordHash);
        Assert.Equal(0, store.SaveChangesCallCount);
    }

    [Fact]
    public async Task ChangePassword_Empty_Stored_Hash_Returns_PasswordNotSet()
    {
        var user = TestData.User(UserId, "user@test.local", passwordHash: string.Empty);
        var store = new FakeUserAuthStore().Seed(user);
        var service = CreateService(store, UserId);

        var result = await service.ChangePasswordAsync("WhateverPass1", "NewPass2");

        Assert.False(result.IsSuccess);
        Assert.Equal("auth.password_not_set", result.Error!.Code);
        Assert.Equal(0, store.SaveChangesCallCount);
    }

    [Fact]
    public async Task ChangePassword_New_Equals_Current_Returns_PasswordUnchanged()
    {
        var user = TestData.User(UserId, "user@test.local", passwordHash: "hashed:OldPass1");
        var store = new FakeUserAuthStore().Seed(user);
        var service = CreateService(store, UserId);

        var result = await service.ChangePasswordAsync("OldPass1", "OldPass1");

        Assert.False(result.IsSuccess);
        Assert.Equal("auth.password_unchanged", result.Error!.Code);
        Assert.Equal(0, store.SaveChangesCallCount);
    }

    [Fact]
    public async Task ChangePassword_RealHasher_EndToEnd_Changes_Hash()
    {
        var user = TestData.User(UserId, "user@test.local", passwordHash: BCrypt.Net.BCrypt.HashPassword("OldPassword1"));
        var store = new FakeUserAuthStore().Seed(user);
        var service = CreateServiceWithRealHasher(store, UserId);

        var result = await service.ChangePasswordAsync("OldPassword1", "NewPassword2");

        Assert.True(result.IsSuccess);
        Assert.Equal(1, store.SaveChangesCallCount);
        Assert.True(BCrypt.Net.BCrypt.Verify("NewPassword2", user.PasswordHash));
        Assert.NotEqual("NewPassword2", user.PasswordHash);
    }

    // --- Fix 1: BCrypt 72-byte truncation cap (auth.password_too_long) ---
    // Applies where a password is CREATED (RegisterAsync's Password, ChangePasswordAsync's
    // newPassword); must NOT apply where a password is SUBMITTED FOR VERIFICATION (LoginAsync's
    // Password, ChangePasswordAsync's currentPassword). See PasswordPolicy.

    [Fact]
    public async Task Register_Password_Of_73_Ascii_Bytes_Returns_PasswordTooLong()
    {
        var store = new FakeUserAuthStore();
        var service = CreateService(store, currentUserId: null);

        var result = await service.RegisterAsync(new RegisterRequest
        {
            Email = "toolong@test.local",
            Password = new string('a', 73), // 73 bytes ASCII, one over the 72-byte cap
            FirstName = "Test",
            LastName = "User",
            PhoneNumber = "+37411111111"
        });

        Assert.False(result.IsSuccess);
        Assert.Equal("auth.password_too_long", result.Error!.Code);
        Assert.Equal(0, store.SaveChangesCallCount);
    }

    [Fact]
    public async Task ChangePassword_New_Password_Of_37_Armenian_Chars_Exceeds_72_Utf8_Bytes_Returns_PasswordTooLong()
    {
        // Armenian 'ա' is 2 bytes in UTF-8, so 37 chars = 74 bytes — over the cap even though
        // string.Length (37) is well under the DTO's 128-char MaxLength. Proves the check counts
        // UTF-8 bytes, not chars (a char-based cap would wrongly accept this).
        var user = TestData.User(UserId, "user@test.local", passwordHash: "hashed:OldPass1");
        var store = new FakeUserAuthStore().Seed(user);
        var service = CreateService(store, UserId);

        var result = await service.ChangePasswordAsync("OldPass1", new string('ա', 37));

        Assert.False(result.IsSuccess);
        Assert.Equal("auth.password_too_long", result.Error!.Code);
        Assert.Equal("hashed:OldPass1", user.PasswordHash);
        Assert.Equal(0, store.SaveChangesCallCount);
    }

    [Fact]
    public async Task Login_Accepts_Password_Over_72_Bytes_Uncapped_On_Verification_Side()
    {
        // Verification-side lengths are intentionally NOT capped: someone who already holds a
        // >72-byte password (e.g. set before this cap existed) must still be able to log in.
        // Real hasher required — this exercises actual BCrypt behavior, not string equality.
        var longPassword = new string('x', 100);
        var user = TestData.User(UserId, "longpw@test.local", passwordHash: BCrypt.Net.BCrypt.HashPassword(longPassword));
        var store = new FakeUserAuthStore().Seed(user);
        var service = CreateServiceWithRealHasher(store, UserId);

        var result = await service.LoginAsync(new LoginRequest { Email = "longpw@test.local", Password = longPassword });

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task ChangePassword_Accepts_CurrentPassword_Over_72_Bytes_Uncapped_On_Verification_Side()
    {
        // Same as above but for ChangePasswordAsync's currentPassword: the cap must apply only
        // to newPassword (being created), not currentPassword (being verified).
        var longCurrentPassword = new string('x', 100);
        var user = TestData.User(UserId, "user@test.local", passwordHash: BCrypt.Net.BCrypt.HashPassword(longCurrentPassword));
        var store = new FakeUserAuthStore().Seed(user);
        var service = CreateServiceWithRealHasher(store, UserId);

        var result = await service.ChangePasswordAsync(longCurrentPassword, "NewPassword2");

        Assert.True(result.IsSuccess);
        Assert.True(BCrypt.Net.BCrypt.Verify("NewPassword2", user.PasswordHash));
    }

    [Fact]
    public async Task ChangePassword_Wrong_CurrentPassword_Sharing_72Byte_Prefix_Of_Real_Password_Is_Rejected()
    {
        // The actual security assertion behind Fix 1. Before the cap, BCrypt.Verify truncates
        // ANY submitted password to 72 bytes before comparing, so a real password >72 bytes
        // could be "proven" with a wrong guess that only matches the first 72 bytes (an attacker
        // who doesn't know the whole secret still gets in). Now that RegisterAsync enforces the
        // cap on creation, no account's real password can ever reach that >=72-byte truncation
        // zone in the first place, so this attack has nothing to exploit for any account created
        // through this codebase. Registers through the real (capped) flow and attacks it through
        // the real (uncapped-on-verification) flow, so this is an end-to-end regression, not just
        // a unit check of the guard clause. Must use the real BcryptPasswordHasher —
        // FakePasswordHasher does exact string equality and can't exhibit (or catch a regression
        // of) BCrypt's truncation behavior at all — see M-013.
        var store = new FakeUserAuthStore();
        var registerService = CreateServiceWithRealHasher(store, currentUserId: null);

        var realPassword = new string('x', 70); // under the cap, so never truncated by BCrypt
        var registerResult = await registerService.RegisterAsync(new RegisterRequest
        {
            Email = "collision@test.local",
            Password = realPassword,
            FirstName = "Test",
            LastName = "User",
            PhoneNumber = "+37411111111"
        });
        Assert.True(registerResult.IsSuccess);
        var registered = Assert.Single(store.Users);
        // Registration no longer signs anyone in (ADR-028); this test is about the password, not the gate.
        registered.IsEmailConfirmed = true;
        var userId = registered.Id;

        // An attacker who only knows the real password's first bytes pads a guess past 72 bytes
        // hoping BCrypt's truncation makes it verify anyway.
        var wrongGuessSharingPrefix = realPassword + new string('z', 30);

        var changeService = CreateServiceWithRealHasher(store, userId);
        var result = await changeService.ChangePasswordAsync(wrongGuessSharingPrefix, "NewPassword2");

        Assert.False(result.IsSuccess);
        Assert.Equal("auth.invalid_current_password", result.Error!.Code);
    }

    // Covers the LoginAsync empty-hash guard (section 3 of the change-password spec):
    // external-auth users are created with PasswordHash = string.Empty, and
    // BCrypt.Net.BCrypt.Verify throws SaltParseException on an empty hash instead of
    // returning false. Must use the real hasher — FakePasswordHasher returns false on an
    // empty hash and would hide this bug entirely (M-013 trap).
    [Fact]
    public async Task Login_With_Empty_Stored_Hash_Returns_InvalidCredentials_Without_Throwing()
    {
        var user = TestData.User(UserId, "external@test.local", passwordHash: string.Empty);
        var store = new FakeUserAuthStore().Seed(user);
        var service = CreateServiceWithRealHasher(store, UserId);

        var result = await service.LoginAsync(new LoginRequest { Email = "external@test.local", Password = "SomePassword1" });

        Assert.False(result.IsSuccess);
        Assert.Equal("auth.invalid_credentials", result.Error!.Code);
    }
}
