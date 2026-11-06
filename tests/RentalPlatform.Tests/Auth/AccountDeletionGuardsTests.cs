using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RentalPlatform.Application.DTOs;
using RentalPlatform.Application.Services;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Infrastructure.Persistence;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Auth;

// ADR-056 amendment 2026-11-04 sections 5-6: login, Google sign-in, password reset and booking creation
// refuse a deleted row EXPLICITLY. Each test builds a row that still carries a usable credential
// (a real hash, a provider link, a live reset token, a real email) and sets only DeletedAt, so the
// refusal can come from nothing but the DeletedAt rule - not from the scrubbing the deletion does.
public sealed class AccountDeletionGuardsTests
{
    private const string Email = "ghost@test.local";
    private const string Password = "Secret1234";
    private static readonly Guid UserId = new("b2000000-0000-0000-0000-000000000001");
    private static readonly DateTime DeletedAt = new(2026, 11, 6, 9, 0, 0, DateTimeKind.Utc);

    private static User DeletedWithCredentials()
    {
        var user = TestData.User(UserId, Email, passwordHash: $"hashed:{Password}");
        user.DeletedAt = DeletedAt;
        user.ExternalAuthProvider = "google";
        user.ExternalProviderId = "g-sub-ghost";
        return user;
    }

    [Fact]
    public async Task Login_Refuses_A_Deleted_Row_Exactly_Like_An_Unknown_Email()
    {
        using var db = new SqliteTestDatabase();
        await db.SeedAsync(DeletedWithCredentials());
        await using var context = db.CreateContext();
        var h = new AuthHarness(context);

        var deleted = await h.Auth.LoginAsync(new LoginRequest { Email = Email, Password = Password });
        var unknown = await h.Auth.LoginAsync(new LoginRequest { Email = "nobody@test.local", Password = Password });

        Assert.Equal("auth.invalid_credentials", deleted.Error!.Code);
        Assert.Equal(unknown.Error!.Code, deleted.Error.Code);
        Assert.Equal(unknown.Error.Message, deleted.Error.Message);
    }

    [Fact]
    public async Task Google_Sign_In_Refuses_A_Deleted_Row_Found_By_Its_Provider_Link()
    {
        using var db = new SqliteTestDatabase();
        await db.SeedAsync(DeletedWithCredentials());
        await using var context = db.CreateContext();
        var h = new AuthHarness(context);
        h.External.Result = new ExternalUserInfo
        {
            Provider = "google", ProviderUserId = "g-sub-ghost", Email = "someone@gmail.com", FirstName = "G", LastName = "G", FullName = "G G"
        };

        var result = await h.Auth.ExternalAsync(new ExternalAuthRequest { Provider = "google", IdToken = "t" });

        Assert.False(result.IsSuccess);
        Assert.Equal("auth.external_invalid_token", result.Error!.Code);
        Assert.Equal(1, await context.Users.CountAsync());
    }

    [Fact]
    public async Task Google_Sign_In_Refuses_A_Deleted_Row_Found_By_Its_Email()
    {
        using var db = new SqliteTestDatabase();
        var user = DeletedWithCredentials();
        user.Email = "ghost@gmail.com";
        user.ExternalAuthProvider = null;
        user.ExternalProviderId = null;
        await db.SeedAsync(user);
        await using var context = db.CreateContext();
        var h = new AuthHarness(context);
        h.External.Result = new ExternalUserInfo
        {
            Provider = "google", ProviderUserId = "g-sub-new", Email = "ghost@gmail.com", FirstName = "G", LastName = "G", FullName = "G G"
        };

        var result = await h.Auth.ExternalAsync(new ExternalAuthRequest { Provider = "google", IdToken = "t" });

        Assert.False(result.IsSuccess);
        Assert.Equal("auth.external_invalid_token", result.Error!.Code);
        var stored = await context.Users.AsNoTracking().SingleAsync();
        Assert.Null(stored.ExternalProviderId); // not linked
    }

    [Fact]
    public async Task Google_Sign_In_With_The_Same_Google_Account_After_Deletion_Creates_A_Fresh_Account()
    {
        using var db = new SqliteTestDatabase();
        // What the deletion leaves behind: scrubbed email, no provider link.
        var user = TestData.User(UserId, $"deleted-{UserId}@deleted.invalid", passwordHash: string.Empty);
        user.DeletedAt = DeletedAt;
        await db.SeedAsync(user);
        await using var context = db.CreateContext();
        var h = new AuthHarness(context);
        h.External.Result = new ExternalUserInfo
        {
            Provider = "google", ProviderUserId = "g-sub-ghost", Email = "returning@gmail.com", FirstName = "Back", LastName = "Again", FullName = "Back Again"
        };

        var result = await h.Auth.ExternalAsync(new ExternalAuthRequest { Provider = "google", IdToken = "t" });

        Assert.True(result.IsSuccess);
        Assert.NotEqual(UserId, result.Value!.User.Id);
        Assert.Equal("returning@gmail.com", result.Value.User.Email);
    }

    [Fact]
    public async Task Forgot_Password_Sends_Nothing_For_A_Deleted_Row_And_Answers_Like_Any_Unknown_Email()
    {
        using var db = new SqliteTestDatabase();
        await db.SeedAsync(DeletedWithCredentials());
        await using var context = db.CreateContext();
        var h = new AuthHarness(context);

        var result = await h.Auth.ForgotPasswordAsync(new ForgotPasswordRequest { Email = Email });

        Assert.True(result.IsSuccess);
        Assert.Empty(h.Sender.Messages);
        Assert.Empty(await context.UserTokens.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task A_Live_Reset_Token_Of_A_Deleted_Row_Is_Invalid_And_Changes_Nothing()
    {
        using var db = new SqliteTestDatabase();
        var live = Guid.NewGuid();
        var raw = "raw-reset-token-of-a-deleted-user";
        await db.SeedAsync(DeletedWithCredentials());
        await db.SeedAsync(new UserToken
        {
            Id = live,
            UserId = UserId,
            Purpose = TokenPurpose.PasswordReset,
            TokenHash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw)),
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(30)
        });
        await using var context = db.CreateContext();
        var h = new AuthHarness(context);

        var check = await h.Auth.CheckResetTokenAsync(new ResetPasswordCheckRequest { Token = raw });
        var reset = await h.Auth.ResetPasswordAsync(new ResetPasswordRequest { Token = raw, NewPassword = "BrandNewPassword9" });

        Assert.Equal("auth.reset_token_invalid", check.Error!.Code);
        Assert.Equal("auth.reset_token_invalid", reset.Error!.Code);
        await using var verify = db.CreateContext();
        var stored = await verify.Users.AsNoTracking().SingleAsync();
        Assert.Equal($"hashed:{Password}", stored.PasswordHash);
        Assert.Equal(0, stored.TokenVersion);
    }

    // ---- booking creation ------------------------------------------------------------------------------

    private static readonly Guid RenterId = new("b2000000-0000-0000-0000-000000000002");
    private static readonly Guid CategoryId = new("b2000000-0000-0000-0000-000000000003");
    private static readonly Guid ListingId = new("b2000000-0000-0000-0000-000000000004");

    private static async Task SeedDeletedOwnersApprovedListingAsync(SqliteTestDatabase db)
    {
        var owner = TestData.OwnerWithHome(UserId, Email);
        owner.DeletedAt = DeletedAt;
        await db.SeedAsync(owner, TestData.User(RenterId, "renter@test.local"), TestData.Category(CategoryId));
        // Approved on purpose: a deletion in progress has not archived the listing yet.
        await db.SeedAsync(TestData.Listing(ListingId, UserId, CategoryId, ListingStatus.Approved));
    }

    private static BookingsService BookingService(AppDbContext context) => new(
        new FakeCurrentUserContext(RenterId),
        new BookingsStore(context),
        new FakeNotificationEmitter(),
        new FakeChatSystemMessageEmitter(),
        new ReviewsStore(context),
        new ConversationsStore(context, NullLogger<ConversationsStore>.Instance));

    private static CreateBookingRequest Request() => new()
    {
        ListingId = ListingId,
        StartDate = TestData.Today.AddDays(6),
        EndDate = TestData.Today.AddDays(7)
    };

    [Fact]
    public async Task Booking_Create_Refuses_A_Listing_Whose_Owner_Is_Deleted_And_Creates_Nothing()
    {
        using var db = new SqliteTestDatabase();
        await SeedDeletedOwnersApprovedListingAsync(db);
        await using var context = db.CreateContext();

        var result = await BookingService(context).CreateAsync(Request());

        Assert.Equal("booking.listing_not_found", result.Error!.Code);
        await using var verify = db.CreateContext();
        Assert.Empty(await verify.Bookings.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task The_Store_Refuses_Inside_Its_Transaction_When_The_Owner_Was_Deleted_After_The_Service_Read()
    {
        using var db = new SqliteTestDatabase();
        await SeedDeletedOwnersApprovedListingAsync(db);
        await using var context = db.CreateContext();
        var store = new BookingsStore(context);
        var booking = TestData.Booking(Guid.NewGuid(), ListingId, RenterId, TestData.Today.AddDays(6), TestData.Today.AddDays(7), BookingStatus.Pending);

        var added = await store.TryCreateBookingAsync(booking);

        Assert.False(added);
        Assert.True(await store.IsListingOwnerDeletedAsync(ListingId));
        await using var verify = db.CreateContext();
        Assert.Empty(await verify.Bookings.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Booking_Create_Still_Works_For_A_Live_Owner()
    {
        using var db = new SqliteTestDatabase();
        await SeedDeletedOwnersApprovedListingAsync(db);
        await using (var fix = db.CreateContext())
        {
            await fix.Users.Where(user => user.Id == UserId).ExecuteUpdateAsync(set => set.SetProperty(user => user.DeletedAt, (DateTime?)null));
        }

        await using var context = db.CreateContext();
        var result = await BookingService(context).CreateAsync(Request());

        Assert.True(result.IsSuccess);
    }
}
