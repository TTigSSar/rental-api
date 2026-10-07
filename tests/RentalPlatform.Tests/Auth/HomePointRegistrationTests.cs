using System.ComponentModel.DataAnnotations;
using RentalPlatform.Application.DTOs;
using RentalPlatform.Application.Services;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Auth;

// The optional home-point step on sign-up. The product rule is "skippable at registration, required
// before publishing", so the contract here has to allow BOTH shapes cleanly — and reject the one
// shape that is neither (half a coordinate).
//
// Validation is asserted against the DTO itself because that is where it lives (DataAnnotations plus
// IValidatableObject, run by the framework before any service sees the request); the service tests
// below then prove the point is actually applied, through the same single writer everything else
// uses.
public sealed class HomePointRegistrationTests
{
    private static readonly Guid UserId = new("b1000000-0000-0000-0000-000000000001");

    private static IReadOnlyList<ValidationResult> Validate(object request)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(request, new ValidationContext(request), results, validateAllProperties: true);
        return results;
    }

    private static RegisterRequest ValidRegister(decimal? latitude = null, decimal? longitude = null) => new()
    {
        Email = "new.owner@test.local",
        Password = "Sufficient1Password",
        FirstName = "New",
        LastName = "Owner",
        PhoneNumber = "+374 99 123456",
        HomeLatitude = latitude,
        HomeLongitude = longitude
    };

    // ---- Contract shape -------------------------------------------------------------------------

    [Fact]
    public void Register_Accepts_A_Request_With_No_Home_Point()
    {
        Assert.Empty(Validate(ValidRegister()));
    }

    [Fact]
    public void Register_Accepts_A_Request_With_Both_Coordinates()
    {
        Assert.Empty(Validate(ValidRegister(TestData.KentronPoint.Latitude, TestData.KentronPoint.Longitude)));
    }

    // Half a coordinate is not a point. Accepting it would store a user "with a home point" whose
    // derivation is impossible, and the failure would surface much later, at publish time.
    [Theory]
    [InlineData(40.1856, null)]
    [InlineData(null, 44.5126)]
    public void Register_Rejects_Only_One_Coordinate(double? latitude, double? longitude)
    {
        var results = Validate(ValidRegister((decimal?)latitude, (decimal?)longitude));

        Assert.Contains(results, r =>
            r.MemberNames.Contains(nameof(RegisterRequest.HomeLatitude)) ||
            r.MemberNames.Contains(nameof(RegisterRequest.HomeLongitude)));
    }

    // Where the point IS is deliberately not a DataAnnotations concern: "inside Yerevan" is a
    // point-in-polygon lookup against the district boundary asset, which is a service. The DTO
    // therefore accepts a well-formed coordinate anywhere on Earth, and the service refuses it —
    // see Register_Rejects_A_Home_Point_Outside_Yerevan below. Keeping that rule in exactly one
    // place is what stops the map and the save from disagreeing.
    [Fact]
    public void Register_Does_Not_Decide_Where_The_Point_Is_At_The_DTO_Layer()
    {
        // London — a perfectly valid WGS84 coordinate, nowhere near the service area.
        Assert.Empty(Validate(ValidRegister(51.5074m, -0.1278m)));
    }

    [Theory]
    [InlineData(95.0, 44.5126)]   // latitude outside WGS84
    [InlineData(40.1856, 200.0)]  // longitude outside WGS84
    public void Register_Rejects_A_Coordinate_Outside_WGS84_Range(double latitude, double longitude)
    {
        Assert.NotEmpty(Validate(ValidRegister((decimal)latitude, (decimal)longitude)));
    }

    // ---- What registration actually stores --------------------------------------------------------

    [Fact]
    public async Task Register_Without_A_Home_Point_Creates_A_User_Who_Has_None()
    {
        var store = new FakeUserAuthStore();
        var service = CreateService(store);

        var result = await service.RegisterAsync(ValidRegister());

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value!.User.HomePoint);
        Assert.Null(Assert.Single(store.Users).HomeLatitude);
    }

    // The exact point is stored, and the derived half is computed at the same time — a user must
    // never be left holding an exact point with no published pair, since that is what their future
    // listings will copy.
    [Fact]
    public async Task Register_With_A_Home_Point_Stores_It_And_Its_Derived_Values()
    {
        var store = new FakeUserAuthStore();
        var districtIds = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase)
        {
            ["kentron"] = TestData.KentronDistrictId
        };
        var homePointService = new FakeHomePointService(store, districtIds);
        var service = CreateService(store, homePointService);

        var (latitude, longitude) = TestData.KentronPoint;
        var result = await service.RegisterAsync(ValidRegister(latitude, longitude));

        Assert.True(result.IsSuccess);

        var homePoint = result.Value!.User.HomePoint;
        Assert.NotNull(homePoint);
        Assert.Equal(latitude, homePoint!.Latitude);
        Assert.Equal(longitude, homePoint.Longitude);
        Assert.NotNull(homePoint.PublicLatitude);
        // Published ≠ exact: the snapped pair is what anyone else would ever see (ADR-008).
        Assert.NotEqual(latitude, homePoint.PublicLatitude);
        Assert.NotNull(homePoint.UpdatedAt);

        var stored = Assert.Single(store.Users);
        Assert.Equal(latitude, stored.HomeLatitude);
        Assert.Equal(TestData.KentronDistrictId, stored.HomeDistrictId);

        // Routed through the one writer, not written inline by AuthService.
        Assert.Single(homePointService.SetCalls);
    }

    // DoRent operates in Yerevan only, and that applies to everyone signing up — not just to people
    // who intend to list a toy. Gyumri is a real Armenian city and is still refused: being in the
    // right country buys nothing.
    [Theory]
    [InlineData(40.7894, 43.8475)]   // Gyumri
    [InlineData(51.5074, -0.1278)]   // London
    public async Task Register_Rejects_A_Home_Point_Outside_Yerevan(double latitude, double longitude)
    {
        var store = new FakeUserAuthStore();
        var service = CreateService(store);

        var result = await service.RegisterAsync(ValidRegister((decimal)latitude, (decimal)longitude));

        Assert.False(result.IsSuccess);
        Assert.Equal("auth.home_point_outside_yerevan", result.Error!.Code);
    }

    // And the account must not exist afterwards. Rejecting AFTER the insert would strand a
    // registered user whose obvious retry then fails on a duplicate email instead of on the pin —
    // the user would be locked out by their own first mistake.
    [Fact]
    public async Task Register_Creates_No_Account_When_The_Home_Point_Is_Refused()
    {
        var store = new FakeUserAuthStore();
        var service = CreateService(store);

        var (latitude, longitude) = TestData.OutsideYerevanPoint;
        await service.RegisterAsync(ValidRegister(latitude, longitude));

        Assert.Empty(store.Users);
        Assert.Equal(0, store.SaveChangesCallCount);
    }

    private static AuthService CreateService(FakeUserAuthStore store, FakeHomePointService? homePointService = null) =>
        new(
            store,
            new FakePasswordHasher(),
            new FakeJwtTokenService(),
            new FakeCurrentUserContext(UserId),
            new FakeExternalIdentityTokenValidator(),
            homePointService ?? new FakeHomePointService(store));
}
