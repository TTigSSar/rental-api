using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Infrastructure.Persistence;
using RentalPlatform.Infrastructure.Services;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Infrastructure;

// ADR-025 amendment: every notification kind is rendered in the RECIPIENT's PreferredLanguage at
// emit time, dates and "N days" included. Rows store finished strings, so this is the only place
// the copy can be checked.
public sealed class BookingNotificationCopyTests
{
    private static readonly Guid OwnerId = new("e8000000-0000-0000-0000-000000000001");
    private static readonly Guid RenterId = new("e8000000-0000-0000-0000-000000000002");
    private static readonly Guid CategoryId = new("e8000000-0000-0000-0000-000000000003");
    private static readonly Guid ListingId = new("e8000000-0000-0000-0000-000000000004");

    private static readonly DateOnly Start = new(2026, 10, 5);
    private static readonly DateOnly End = new(2026, 10, 7);

    private const string Toy = "Wooden Train Set";

    private sealed record Setup(Booking Booking, User Owner, User Renter, Listing Listing);

    private static Setup Build(string? ownerLanguage, string? renterLanguage, DateOnly? end = null, decimal total = 1500m)
    {
        var owner = TestData.User(OwnerId, "owner@test.local", firstName: "Anahit", lastName: "Grigoryan", preferredLanguage: ownerLanguage);
        var renter = TestData.User(RenterId, "renter@test.local", firstName: "Karen", lastName: "Petrosyan", preferredLanguage: renterLanguage);
        var listing = TestData.Listing(ListingId, OwnerId, CategoryId);
        listing.Title = Toy;
        listing.Owner = owner;

        var booking = TestData.Booking(Guid.NewGuid(), ListingId, RenterId, Start, end ?? End, BookingStatus.Pending);
        booking.TotalPrice = total;
        booking.Listing = listing;
        booking.Renter = renter;
        return new Setup(booking, owner, renter, listing);
    }

    private static async Task<Notification> Emit(Setup s, Func<NotificationEmitter, Task> act, Guid recipientId)
    {
        using var db = new SqliteTestDatabase();
        // Only the recipient needs to exist (Notification FK); the in-memory graph carries the rest.
        var recipient = recipientId == OwnerId ? s.Owner : s.Renter;
        await db.SeedAsync(TestData.User(recipient.Id, recipient.Email, preferredLanguage: recipient.PreferredLanguage));

        await using var context = db.CreateContext();
        var emitter = new NotificationEmitter(new NotificationsStore(context), NullLogger<NotificationEmitter>.Instance);
        await act(emitter);
        return Assert.Single(context.Notifications);
    }

    private static Task<Notification> Requested(string? ownerLanguage, DateOnly? end = null, decimal total = 1500m)
    {
        var s = Build(ownerLanguage, "en", end, total);
        return Emit(s, e => e.BookingRequestedAsync(s.Booking, s.Renter, s.Listing), OwnerId);
    }

    private static Task<Notification> Approved(string? renterLanguage)
    {
        var s = Build("en", renterLanguage);
        return Emit(s, e => e.BookingApprovedAsync(s.Booking, s.Owner), RenterId);
    }

    private static Task<Notification> Declined(string? renterLanguage, string? reason = null)
    {
        var s = Build("en", renterLanguage);
        s.Booking.RejectionReason = reason;
        return Emit(s, e => e.BookingDeclinedAsync(s.Booking, s.Owner), RenterId);
    }

    private static Task<Notification> ListingLive(string? ownerLanguage)
    {
        var s = Build(ownerLanguage, "en");
        return Emit(s, e => e.ListingApprovedAsync(s.Listing), OwnerId);
    }

    private static Task<Notification> ListingChanges(string? ownerLanguage, string? note, string? code = null)
    {
        var s = Build(ownerLanguage, "en");
        return Emit(s, e => e.ListingRejectedAsync(s.Listing, code, note), OwnerId);
    }

    private static readonly Regex Latin = new("[A-Za-z]", RegexOptions.Compiled);
    private static readonly Regex Cyrillic = new("[Ѐ-ӿ]", RegexOptions.Compiled);
    private static readonly Regex Armenian = new("[԰-֏]", RegexOptions.Compiled);

    // ---- ICU / culture sanity ----------------------------------------------------------------

    [Fact]
    public void The_Runtime_Has_Real_Russian_And_Armenian_Month_Names()
    {
        // Fails loudly if the test host runs without ICU (InvariantGlobalization) — in that case
        // every non-English date would silently come out English.
        Assert.Equal("5 Oct", NotificationCopy.FormatRange(Start, Start, "en"));
        Assert.Equal("5 окт.", NotificationCopy.FormatRange(Start, Start, "ru"));

        var hy = NotificationCopy.FormatRange(Start, Start, "hy");
        Assert.NotEqual("5 Oct", hy);
        Assert.Matches(Armenian, hy);
        Assert.DoesNotMatch(Latin, hy);
    }

    [Theory]
    [InlineData("en", "5 Oct–7 Oct")]
    [InlineData("ru", "5 окт.–7 окт.")]
    public void Ranges_Use_Day_Month_And_Collapse_To_A_Single_Date(string language, string expectedRange)
    {
        Assert.Equal(expectedRange, NotificationCopy.FormatRange(Start, End, language));
        Assert.DoesNotContain("–", NotificationCopy.FormatRange(Start, Start, language), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ru", 1, "день")]
    [InlineData("ru", 2, "дня")]
    [InlineData("ru", 4, "дня")]
    [InlineData("ru", 5, "дней")]
    [InlineData("ru", 11, "дней")]
    [InlineData("ru", 12, "дней")]
    [InlineData("ru", 21, "день")]
    [InlineData("ru", 22, "дня")]
    [InlineData("ru", 25, "дней")]
    [InlineData("hy", 1, "օր")]
    [InlineData("hy", 5, "օր")]
    [InlineData("en", 1, "day")]
    [InlineData("en", 2, "days")]
    public void Day_Labels_Use_The_Correct_Plural_Form(string language, int days, string expected)
    {
        Assert.Equal(expected, NotificationCopy.DayLabel(language, days));
    }

    [Fact]
    public void Amount_Format_Is_Identical_In_Every_Language()
    {
        Assert.Equal("2 days · 1,500 ֏", NotificationCopy.BookingMeta("en", 2, 1500m));
        Assert.Equal("2 дня · 1,500 ֏", NotificationCopy.BookingMeta("ru", 2, 1500m));
        Assert.Equal("2 օր · 1,500 ֏", NotificationCopy.BookingMeta("hy", 2, 1500m));
    }

    // ---- Request (to the owner) ----------------------------------------------------------------

    [Fact]
    public async Task Request_En()
    {
        var n = await Requested("en");
        Assert.Equal(NotificationKind.Request, n.Kind);
        Assert.Equal("New rental request from Karen", n.Title);
        Assert.Contains("wants your \"Wooden Train Set\" for 5 Oct–7 Oct.", n.Body, StringComparison.Ordinal);
        Assert.Equal("Review request", n.PrimaryActionLabel);
        Assert.Equal("2 days · 1,500 ֏", n.Meta);
    }

    [Fact]
    public async Task Request_Ru()
    {
        var n = await Requested("ru");
        Assert.Equal("Новый запрос на аренду от Karen", n.Title);
        Assert.Contains("на 5 окт.–7 окт.", n.Body, StringComparison.Ordinal);
        Assert.Equal("Посмотреть запрос", n.PrimaryActionLabel);
        Assert.Equal("2 дня · 1,500 ֏", n.Meta);
    }

    [Fact]
    public async Task Request_Hy()
    {
        var n = await Requested("hy");
        Assert.Equal("Նոր վարձակալության հարցում՝ Karen", n.Title);
        Assert.Equal("Դիտել հարցումը", n.PrimaryActionLabel);
        Assert.Matches(Armenian, n.Title);
        Assert.Matches(Armenian, n.Body);
        Assert.Matches(Armenian, n.PrimaryActionLabel);
        Assert.DoesNotContain("Oct", n.Body, StringComparison.Ordinal);
        Assert.Equal("2 օր · 1,500 ֏", n.Meta);
        Assert.Contains(NotificationCopy.FormatRange(Start, End, "hy"), n.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Request_Single_Day_Uses_One_Date_And_Singular_Label()
    {
        var n = await Requested("ru", end: Start, total: 700m);
        Assert.Contains("на 5 окт.", n.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("–", n.Body, StringComparison.Ordinal);
        Assert.Equal("1 день · 700 ֏", n.Meta);
    }

    // ---- Approved (to the renter) --------------------------------------------------------------

    [Fact]
    public async Task Approved_En()
    {
        var n = await Approved("en");
        Assert.Equal(NotificationKind.Approved, n.Kind);
        Assert.Equal("Anahit approved your request", n.Title);
        Assert.Contains("is yours for 5 Oct–7 Oct. Arrange a pickup time with Anahit.", n.Body, StringComparison.Ordinal);
        Assert.Equal("Arrange pickup", n.PrimaryActionLabel);
    }

    [Fact]
    public async Task Approved_Ru()
    {
        var n = await Approved("ru");
        Assert.Equal("Ваш запрос одобрен", n.Title);
        Assert.StartsWith("Игрушка «Wooden Train Set» ваша на 5 окт.–7 окт.", n.Body, StringComparison.Ordinal);
        Assert.Contains("5 окт.–7 окт.", n.Body, StringComparison.Ordinal);
        Assert.Contains("Anahit", n.Body, StringComparison.Ordinal);
        Assert.Equal("2 дня · 1,500 ֏", n.Meta);
        Assert.DoesNotMatch(Latin, n.PrimaryActionLabel);
    }

    [Fact]
    public async Task Approved_Hy()
    {
        var n = await Approved("hy");
        Assert.Matches(Armenian, n.Title);
        Assert.Contains(NotificationCopy.FormatRange(Start, End, "hy"), n.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("Oct", n.Body, StringComparison.Ordinal);
        Assert.Equal("2 օր · 1,500 ֏", n.Meta);
    }

    // ---- Declined (to the renter) --------------------------------------------------------------

    [Fact]
    public async Task Declined_En()
    {
        var n = await Declined("en");
        Assert.Equal(NotificationKind.Declined, n.Kind);
        Assert.Equal("Anahit declined your request", n.Title);
        Assert.Equal("Your request for \"Wooden Train Set\" was declined. Here are similar toys nearby.", n.Body);
        Assert.Equal("Find similar toys", n.PrimaryActionLabel);
    }

    [Fact]
    public async Task Declined_Ru_With_And_Without_Reason()
    {
        var plain = await Declined("ru");
        Assert.Equal("Ваш запрос отклонён", plain.Title);
        Assert.Contains("«Wooden Train Set»", plain.Body, StringComparison.Ordinal);

        var withReason = await Declined("ru", "Игрушка в ремонте");
        Assert.Equal("«Игрушка в ремонте» — вот похожие игрушки поблизости.", withReason.Body);
    }

    [Fact]
    public async Task Declined_Hy_With_And_Without_Reason()
    {
        var plain = await Declined("hy");
        Assert.Matches(Armenian, plain.Title);
        Assert.Matches(Armenian, plain.Body);

        var withReason = await Declined("hy", "Reason text");
        Assert.StartsWith("«Reason text»", withReason.Body, StringComparison.Ordinal);
    }

    // ---- Listing kinds (to the owner) ----------------------------------------------------------

    [Theory]
    [InlineData("en", "Your listing is live")]
    [InlineData("ru", "Ваше объявление опубликовано")]
    public async Task ListingLive_Title(string language, string expectedTitle)
    {
        var n = await ListingLive(language);
        Assert.Equal(NotificationKind.ListingLive, n.Kind);
        Assert.Equal(expectedTitle, n.Title);
        Assert.Contains(Toy, n.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListingLive_Hy()
    {
        var n = await ListingLive("hy");
        Assert.Matches(Armenian, n.Title);
        Assert.Matches(Armenian, n.PrimaryActionLabel);
    }

    [Theory]
    [InlineData("en", "Changes needed on your listing")]
    [InlineData("ru", "Нужны правки в объявлении")]
    public async Task ListingChanges_Title_And_Reason(string language, string expectedTitle)
    {
        var n = await ListingChanges(language, "Photos are blurry.");
        Assert.Equal(NotificationKind.ListingChanges, n.Kind);
        Assert.Equal(expectedTitle, n.Title);
        Assert.StartsWith("Photos are blurry.", n.Body, StringComparison.Ordinal);

        var none = await ListingChanges(language, null);
        Assert.DoesNotContain("Photos", none.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListingChanges_Hy()
    {
        var n = await ListingChanges("hy", null);
        Assert.Matches(Armenian, n.Title);
        Assert.Matches(Armenian, n.Body);
    }

    // ---- Moderation rejection reason label (code -> recipient language) ---------------------------

    public static IEnumerable<object[]> ReasonLabels => new[]
    {
        new object[] { "poorImages", "Poor or missing images", "Плохие фото", "Վատ լուսանկարներ" },
        new object[] { "missingInfo", "Missing or incomplete information", "Недостаточно информации", "Բացակայող տեղեկատվություն" },
        new object[] { "duplicate", "Duplicate listing", "Дублирующее объявление", "Կրկնվող հայտարարություն" },
        new object[] { "inappropriate", "Inappropriate content", "Неподходящий контент", "Անպատշաճ բովանդակություն" },
        new object[] { "wrongCategory", "Listed under incorrect category", "Неверная категория", "Սխալ կատեգորիա" },
        new object[] { "unsafeItem", "Unsafe item", "Небезопасный предмет", "Անվտանգ չէ" },
        new object[] { "hygiene", "No cleaning or hygiene details", "Гигиена", "Հիգիենա" },
        new object[] { "pricing", "Unrealistic pricing", "Цена", "Գնագոյացում" },
        new object[] { "other", "Other", "Другое", "Այլ" },
    };

    [Theory]
    [MemberData(nameof(ReasonLabels))]
    public void Reason_Label_Is_Localized_Per_Code(string code, string en, string ru, string hy)
    {
        Assert.Equal(en, RentalPlatform.Application.Common.RejectionReasonCatalog.LabelFor(code, "en"));
        Assert.Equal(ru, RentalPlatform.Application.Common.RejectionReasonCatalog.LabelFor(code, "ru"));
        Assert.Equal(hy, RentalPlatform.Application.Common.RejectionReasonCatalog.LabelFor(code, "hy"));
        Assert.Equal(en, RentalPlatform.Application.Common.RejectionReasonCatalog.LabelFor(code, "de"));
        Assert.Equal(en, RentalPlatform.Application.Common.RejectionReasonCatalog.LabelFor(code, null));
        Assert.Equal(en, RentalPlatform.Application.Common.RejectionReasonCatalog.LabelFor(code));
    }

    [Theory]
    [MemberData(nameof(ReasonLabels))]
    public async Task ListingChanges_Renders_The_Reason_Label_In_The_Recipient_Language(string code, string en, string ru, string hy)
    {
        var ruBody = (await ListingChanges("ru", "Админская заметка", code)).Body;
        Assert.StartsWith($"{ru}: Админская заметка ", ruBody, StringComparison.Ordinal);
        Assert.DoesNotContain(en, ruBody, StringComparison.Ordinal);

        var hyBody = (await ListingChanges("hy", "note", code)).Body;
        Assert.StartsWith($"{hy}: note ", hyBody, StringComparison.Ordinal);
        Assert.DoesNotContain(en, hyBody, StringComparison.Ordinal);

        var deBody = (await ListingChanges("de", null, code)).Body;
        Assert.StartsWith($"{en} ", deBody, StringComparison.Ordinal);
    }

    // ---- Fallback ------------------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("de")]
    public async Task Unknown_Or_Missing_Language_Falls_Back_To_English_For_Every_Kind(string? language)
    {
        var request = await Requested(language);
        Assert.Equal("New rental request from Karen", request.Title);
        Assert.Contains("5 Oct–7 Oct", request.Body, StringComparison.Ordinal);
        Assert.Equal("2 days · 1,500 ֏", request.Meta);

        Assert.Equal("Anahit approved your request", (await Approved(language)).Title);
        Assert.Equal("Anahit declined your request", (await Declined(language)).Title);
        Assert.Equal("Your listing is live", (await ListingLive(language)).Title);
        Assert.Equal("Changes needed on your listing", (await ListingChanges(language, null)).Title);
    }

    [Fact]
    public async Task Regional_Tag_Resolves_To_The_Language()
    {
        var n = await Approved("ru-RU");
        Assert.Equal("Ваш запрос одобрен", n.Title);
    }

    // A booking whose Renter navigation was never loaded must still emit (in English) rather than throw.
    [Fact]
    public async Task Missing_Recipient_Navigation_Falls_Back_To_English()
    {
        var s = Build("en", "ru");
        s.Booking.Renter = null!;
        var n = await Emit(s, e => e.BookingApprovedAsync(s.Booking, s.Owner), RenterId);
        Assert.Equal("Anahit approved your request", n.Title);
    }

    // ---- Sentence stops after a date that ends with an abbreviation dot ---------------------------

    [Theory]
    [InlineData("en")]
    [InlineData("ru")]
    [InlineData("hy")]
    public async Task No_Rendered_Body_Contains_A_Doubled_Stop(string language)
    {
        foreach (var body in new[]
        {
            (await Requested(language)).Body,
            (await Requested(language, end: Start)).Body,
            (await Approved(language)).Body,
            (await Declined(language)).Body,
            (await Declined(language, "dates_unavailable")).Body,
            (await ListingLive(language)).Body,
            (await ListingChanges(language, null)).Body
        })
        {
            Assert.DoesNotContain("..", body, StringComparison.Ordinal);
            Assert.DoesNotContain(".։", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EndSentence_Does_Not_Double_An_Abbreviation_Dot()
    {
        Assert.Equal("10 мар.–12 мар.", NotificationCopy.EndSentence("10 мар.–12 мар.", "."));
        Assert.Equal("10 Mar–12 Mar.", NotificationCopy.EndSentence("10 Mar–12 Mar", "."));
        Assert.Equal("10 մար։", NotificationCopy.EndSentence("10 մար.", "։"));
    }

    [Fact]
    public async Task Approved_Ru_Reads_Cleanly_After_The_Dated_Range()
    {
        var n = await Approved("ru");
        Assert.Contains("на 5 окт.–7 окт. Договоритесь", n.Body, StringComparison.Ordinal);
    }

    // ---- Booking rejection reason codes ----------------------------------------------------------

    [Theory]
    [InlineData("dates_unavailable", "en", "Dates are no longer available")]
    [InlineData("dates_unavailable", "ru", "Даты больше недоступны")]
    [InlineData("dates_unavailable", "hy", "Ամսաթվերն այլևս հասանելի չեն")]
    [InlineData("item_unavailable", "en", "The toy is currently unavailable")]
    [InlineData("item_unavailable", "ru", "Игрушка сейчас недоступна")]
    [InlineData("item_unavailable", "hy", "Խաղալիքն այս պահին հասանելի չէ")]
    [InlineData("not_a_fit", "en", "This booking isn't a good fit")]
    [InlineData("not_a_fit", "ru", "Это бронирование не подходит")]
    [InlineData("not_a_fit", "hy", "Այս ամրագրումը հարմար չէ")]
    public async Task Declined_Maps_Known_Reason_Codes_To_Localized_Text(string code, string language, string expected)
    {
        var n = await Declined(language, code);
        Assert.Contains(expected, n.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(code, n.Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("ru")]
    [InlineData("hy")]
    public async Task Declined_Keeps_Free_Text_Verbatim(string language)
    {
        var n = await Declined(language, "Уезжаем на выходные");
        Assert.Contains("Уезжаем на выходные", n.Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("ru")]
    [InlineData("hy")]
    public async Task Declined_Unknown_Code_Shows_No_Reason(string language)
    {
        var generic = await Declined(language);
        foreach (var code in new[] { "some_new_code", "other" })
        {
            var n = await Declined(language, code);
            Assert.Equal(generic.Body, n.Body);
            Assert.DoesNotContain(code, n.Body, StringComparison.Ordinal);
        }
    }
}
