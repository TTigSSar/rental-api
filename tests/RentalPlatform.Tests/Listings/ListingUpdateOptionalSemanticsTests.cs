using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using RentalPlatform.Application.Common;
using RentalPlatform.Application.DTOs;
using RentalPlatform.Application.Services;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Infrastructure.Persistence;
using RentalPlatform.Infrastructure.Services;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Listings;

// ADR-040: PATCH /api/listings/{id} tells an ABSENT property from an explicit null for the clearable
// optionals (ageFromMonths, ageToMonths, condition, hygieneNotes, safetyNotes, minRentalDays), and
// compensationAmount rejects an explicit null (ADR-018).
public sealed class ListingUpdateOptionalSemanticsTests
{
    private static readonly Guid OwnerId = new("d0000000-0000-0000-0000-000000000001");
    private static readonly Guid CategoryId = new("d0000000-0000-0000-0000-000000000002");
    private static readonly Guid ListingId = new("d0000000-0000-0000-0000-000000000003");

    private static ListingsOwnerService CreateService(AppDbContext context) =>
        new(new FakeCurrentUserContext(OwnerId), new ListingsOwnerStore(context));

    private static async Task SeedAsync(SqliteTestDatabase db, ListingStatus status = ListingStatus.Approved, int? from = 12, int? to = 60)
    {
        var listing = TestData.Listing(ListingId, OwnerId, CategoryId, status);
        listing.AgeFromMonths = from;
        listing.AgeToMonths = to;
        listing.Condition = "Good";
        listing.HygieneNotes = "Washed";
        listing.SafetyNotes = "No small parts";
        listing.MinRentalDays = 3;
        listing.CompensationAmount = 5000m;
        await db.SeedAsync(
            TestData.OwnerWithHome(OwnerId, "owner@test.local"),
            TestData.Category(CategoryId),
            listing);
    }

    private static async Task<Listing> LoadAsync(SqliteTestDatabase db)
    {
        await using var verify = db.CreateContext();
        return (await verify.Listings.FindAsync(ListingId))!;
    }

    private static IReadOnlyList<ValidationResult> Validate(UpdateListingRequest request)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(request, new ValidationContext(request), results, validateAllProperties: true);
        return results;
    }

    // ---- Binding: absent vs null vs value through the real System.Text.Json pipeline -----------

    [Fact]
    public void Binding_Distinguishes_Absent_Null_And_Value()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var request = JsonSerializer.Deserialize<UpdateListingRequest>(
            """{"ageFromMonths":null,"ageToMonths":24,"condition":null,"compensationAmount":null}""", options)!;

        Assert.True(request.AgeFromMonths.IsSet);
        Assert.Null(request.AgeFromMonths.Value);
        Assert.True(request.AgeToMonths.IsSet);
        Assert.Equal(24, request.AgeToMonths.Value);
        Assert.True(request.Condition.IsSet);
        Assert.Null(request.Condition.Value);
        Assert.True(request.CompensationAmount.IsSet);
        Assert.Null(request.CompensationAmount.Value);
        Assert.False(request.HygieneNotes.IsSet);
        Assert.False(request.SafetyNotes.IsSet);
        Assert.False(request.MinRentalDays.IsSet);
    }

    [Fact]
    public void Optional_Serialises_As_The_Inner_Value()
    {
        Assert.Equal("7", JsonSerializer.Serialize(new Optional<int?>(7)));
        Assert.Equal("null", JsonSerializer.Serialize(new Optional<string?>(null)));
    }

    // ---- Absent leaves every field unchanged ----------------------------------------------------

    [Fact]
    public async Task Absent_Properties_Leave_All_Seven_Fields_Unchanged()
    {
        using var db = new SqliteTestDatabase();
        await SeedAsync(db);

        await using var context = db.CreateContext();
        var result = await CreateService(context).UpdateAsync(ListingId, new UpdateListingRequest { PricePerDay = 11m });

        Assert.True(result.IsSuccess);
        var stored = await LoadAsync(db);
        Assert.Equal(12, stored.AgeFromMonths);
        Assert.Equal(60, stored.AgeToMonths);
        Assert.Equal("Good", stored.Condition);
        Assert.Equal("Washed", stored.HygieneNotes);
        Assert.Equal("No small parts", stored.SafetyNotes);
        Assert.Equal(3, stored.MinRentalDays);
        Assert.Equal(5000m, stored.CompensationAmount);
        Assert.Equal(ListingStatus.Approved, stored.Status);
    }

    // ---- Explicit null clears each of the six ---------------------------------------------------

    [Fact]
    public async Task Explicit_Null_Clears_Each_Clearable_Optional()
    {
        using var db = new SqliteTestDatabase();
        await SeedAsync(db);

        await using var context = db.CreateContext();
        var result = await CreateService(context).UpdateAsync(ListingId, new UpdateListingRequest
        {
            AgeFromMonths = null,
            AgeToMonths = null,
            Condition = null,
            HygieneNotes = null,
            SafetyNotes = null,
            MinRentalDays = null
        });

        Assert.True(result.IsSuccess);
        var stored = await LoadAsync(db);
        Assert.Null(stored.AgeFromMonths);
        Assert.Null(stored.AgeToMonths);
        Assert.Null(stored.Condition);
        Assert.Null(stored.HygieneNotes);
        Assert.Null(stored.SafetyNotes);
        Assert.Null(stored.MinRentalDays);
        Assert.Equal(5000m, stored.CompensationAmount);
    }

    [Fact]
    public async Task Values_Set_Each_Field()
    {
        using var db = new SqliteTestDatabase();
        await SeedAsync(db);

        await using var context = db.CreateContext();
        var result = await CreateService(context).UpdateAsync(ListingId, new UpdateListingRequest
        {
            AgeFromMonths = 6,
            AgeToMonths = 120,
            Condition = "  Like new ",
            HygieneNotes = "Sanitised",
            SafetyNotes = "Certified",
            MinRentalDays = 7,
            CompensationAmount = 20000m
        });

        Assert.True(result.IsSuccess);
        var stored = await LoadAsync(db);
        Assert.Equal(6, stored.AgeFromMonths);
        Assert.Equal(120, stored.AgeToMonths);
        Assert.Equal("Like new", stored.Condition);
        Assert.Equal("Sanitised", stored.HygieneNotes);
        Assert.Equal("Certified", stored.SafetyNotes);
        Assert.Equal(7, stored.MinRentalDays);
        Assert.Equal(20000m, stored.CompensationAmount);
    }

    [Fact]
    public async Task Blank_Text_Still_Clears()
    {
        using var db = new SqliteTestDatabase();
        await SeedAsync(db);

        await using var context = db.CreateContext();
        var result = await CreateService(context).UpdateAsync(ListingId, new UpdateListingRequest { Condition = "   " });

        Assert.True(result.IsSuccess);
        Assert.Null((await LoadAsync(db)).Condition);
    }

    // ---- Moderation: text clear is a content change, numeric clear is not ----------------------

    [Theory]
    [InlineData("condition")]
    [InlineData("hygiene")]
    [InlineData("safety")]
    public async Task Clearing_A_Text_Field_On_An_Approved_Listing_Requires_Re_Moderation(string field)
    {
        using var db = new SqliteTestDatabase();
        await SeedAsync(db);

        await using var context = db.CreateContext();
        var request = field switch
        {
            "condition" => new UpdateListingRequest { Condition = null },
            "hygiene" => new UpdateListingRequest { HygieneNotes = null },
            _ => new UpdateListingRequest { SafetyNotes = null }
        };
        var result = await CreateService(context).UpdateAsync(ListingId, request);

        Assert.True(result.IsSuccess);
        Assert.Equal(ListingStatus.PendingApproval, (await LoadAsync(db)).Status);
    }

    [Fact]
    public async Task Clearing_Numeric_Fields_On_An_Approved_Listing_Keeps_It_Approved()
    {
        using var db = new SqliteTestDatabase();
        await SeedAsync(db);

        await using var context = db.CreateContext();
        var result = await CreateService(context).UpdateAsync(ListingId, new UpdateListingRequest
        {
            AgeFromMonths = null,
            AgeToMonths = null,
            MinRentalDays = null
        });

        Assert.True(result.IsSuccess);
        Assert.Equal(ListingStatus.Approved, (await LoadAsync(db)).Status);
    }

    // ---- Age range uses the EFFECTIVE post-update values ---------------------------------------

    [Fact]
    public async Task Age_Range_Check_Uses_Stored_From_When_Only_To_Is_Sent()
    {
        using var db = new SqliteTestDatabase();
        await SeedAsync(db, from: 24, to: 60);

        await using var context = db.CreateContext();
        var result = await CreateService(context).UpdateAsync(ListingId, new UpdateListingRequest { AgeToMonths = 12 });

        Assert.False(result.IsSuccess);
        Assert.Equal("listing.invalid_age_range", result.Error!.Code);
        Assert.Equal(60, (await LoadAsync(db)).AgeToMonths);
    }

    [Fact]
    public async Task Age_Range_Check_Uses_Stored_To_When_Only_From_Is_Sent()
    {
        using var db = new SqliteTestDatabase();
        await SeedAsync(db, from: 12, to: 24);

        await using var context = db.CreateContext();
        var result = await CreateService(context).UpdateAsync(ListingId, new UpdateListingRequest { AgeFromMonths = 36 });

        Assert.False(result.IsSuccess);
        Assert.Equal("listing.invalid_age_range", result.Error!.Code);
    }

    [Fact]
    public async Task Age_Range_Is_Not_Checked_When_From_Is_Cleared()
    {
        using var db = new SqliteTestDatabase();
        await SeedAsync(db, from: 24, to: 60);

        await using var context = db.CreateContext();
        var result = await CreateService(context).UpdateAsync(ListingId, new UpdateListingRequest
        {
            AgeFromMonths = null,
            AgeToMonths = 12
        });

        Assert.True(result.IsSuccess);
        var stored = await LoadAsync(db);
        Assert.Null(stored.AgeFromMonths);
        Assert.Equal(12, stored.AgeToMonths);
    }

    // ---- Validation: same messages, now in Validate() ------------------------------------------

    [Fact]
    public void Compensation_Explicit_Null_Is_A_Validation_Error()
    {
        var results = Validate(new UpdateListingRequest { CompensationAmount = null });

        var error = Assert.Single(results);
        Assert.Equal("Loss & damage compensation cannot be cleared.", error.ErrorMessage);
        Assert.Contains(nameof(UpdateListingRequest.CompensationAmount), error.MemberNames);
    }

    [Fact]
    public void Compensation_Absent_Is_Valid()
    {
        Assert.Empty(Validate(new UpdateListingRequest()));
    }

    [Theory]
    [InlineData(nameof(UpdateListingRequest.CompensationAmount), "Loss & damage compensation must be between 1,000 and 10,000,000 AMD.")]
    [InlineData(nameof(UpdateListingRequest.AgeFromMonths), "Age (from, months) must be between 0 and 600.")]
    [InlineData(nameof(UpdateListingRequest.AgeToMonths), "Age (to, months) must be between 0 and 600.")]
    [InlineData(nameof(UpdateListingRequest.MinRentalDays), "Minimum rental days must be between 1 and 365.")]
    [InlineData(nameof(UpdateListingRequest.Condition), "Condition must be at most 50 characters.")]
    [InlineData(nameof(UpdateListingRequest.HygieneNotes), "Hygiene notes must be at most 1000 characters.")]
    [InlineData(nameof(UpdateListingRequest.SafetyNotes), "Safety notes must be at most 1000 characters.")]
    public void Out_Of_Range_Values_Are_Rejected_With_The_Original_Message(string member, string message)
    {
        var request = member switch
        {
            nameof(UpdateListingRequest.CompensationAmount) => new UpdateListingRequest { CompensationAmount = 999m },
            nameof(UpdateListingRequest.AgeFromMonths) => new UpdateListingRequest { AgeFromMonths = 601 },
            nameof(UpdateListingRequest.AgeToMonths) => new UpdateListingRequest { AgeToMonths = -1 },
            nameof(UpdateListingRequest.MinRentalDays) => new UpdateListingRequest { MinRentalDays = 0 },
            nameof(UpdateListingRequest.Condition) => new UpdateListingRequest { Condition = new string('x', 51) },
            nameof(UpdateListingRequest.HygieneNotes) => new UpdateListingRequest { HygieneNotes = new string('x', 1001) },
            _ => new UpdateListingRequest { SafetyNotes = new string('x', 1001) }
        };

        var error = Assert.Single(Validate(request));
        Assert.Equal(message, error.ErrorMessage);
        Assert.Contains(member, error.MemberNames);
    }

    [Fact]
    public void Explicit_Null_Clearables_Are_Valid()
    {
        Assert.Empty(Validate(new UpdateListingRequest
        {
            AgeFromMonths = null,
            AgeToMonths = null,
            Condition = null,
            HygieneNotes = null,
            SafetyNotes = null,
            MinRentalDays = null
        }));
    }
}
