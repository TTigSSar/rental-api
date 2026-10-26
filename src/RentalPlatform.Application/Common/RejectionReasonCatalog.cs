namespace RentalPlatform.Application.Common;

// Single source of truth for the structured listing-rejection reasons.
// Codes are kept in sync with REJECT_REASONS in the Angular admin pending-listings page,
// and the labels are used to compose the human-readable reason stored on the listing and
// emailed to the owner.
public static class RejectionReasonCatalog
{
    public const int MaxReasonLength = 1000;

    private static readonly IReadOnlyDictionary<string, string> Labels =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["poorImages"] = "Poor or missing images",
            ["missingInfo"] = "Missing or incomplete information",
            ["duplicate"] = "Duplicate listing",
            ["inappropriate"] = "Inappropriate content",
            ["wrongCategory"] = "Listed under incorrect category",
            ["unsafeItem"] = "Unsafe item",
            ["hygiene"] = "No cleaning or hygiene details",
            ["pricing"] = "Unrealistic pricing",
            ["other"] = "Other",
        };

    public static bool IsKnownCode(string? code) =>
        !string.IsNullOrWhiteSpace(code) && Labels.ContainsKey(code.Trim());

    // Russian and Armenian labels mirror the UI's admin.pendingListings.rejectSheet.reasons.<code>.title
    // strings exactly. English is the fallback for any other language.
    private static readonly IReadOnlyDictionary<string, string> LabelsRu =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["poorImages"] = "Плохие фото",
            ["missingInfo"] = "Недостаточно информации",
            ["duplicate"] = "Дублирующее объявление",
            ["inappropriate"] = "Неподходящий контент",
            ["wrongCategory"] = "Неверная категория",
            ["unsafeItem"] = "Небезопасный предмет",
            ["hygiene"] = "Гигиена",
            ["pricing"] = "Цена",
            ["other"] = "Другое",
        };

    private static readonly IReadOnlyDictionary<string, string> LabelsHy =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["poorImages"] = "Վատ լուսանկարներ",
            ["missingInfo"] = "Բացակայող տեղեկատվություն",
            ["duplicate"] = "Կրկնվող հայտարարություն",
            ["inappropriate"] = "Անպատշաճ բովանդակություն",
            ["wrongCategory"] = "Սխալ կատեգորիա",
            ["unsafeItem"] = "Անվտանգ չէ",
            ["hygiene"] = "Հիգիենա",
            ["pricing"] = "Գնագոյացում",
            ["other"] = "Այլ",
        };

    public static string LabelFor(string code) => LabelFor(code, "en");

    // language: "en" | "ru" | "hy" (a regional tag such as "ru-RU" is accepted); anything else -> English.
    public static string LabelFor(string code, string? language)
    {
        var key = code.Trim();
        var primary = language?.Trim().Split('-', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        var localized = primary?.ToLowerInvariant() switch
        {
            "ru" => LabelsRu,
            "hy" => LabelsHy,
            _ => null
        };

        if (localized is not null && localized.TryGetValue(key, out var text))
        {
            return text;
        }

        return Labels.TryGetValue(key, out var label) ? label : key;
    }

    // Composes the persisted/emailed reason: "<label>: <note>" — or just the label when no note.
    public static string Compose(string code, string? note) => Compose(code, note, "en");

    public static string Compose(string code, string? note, string? language)
    {
        var label = LabelFor(code, language);
        var trimmedNote = note?.Trim();
        return string.IsNullOrEmpty(trimmedNote) ? label : $"{label}: {trimmedNote}";
    }
}
