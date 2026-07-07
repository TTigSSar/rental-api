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
        };

    public static bool IsKnownCode(string? code) =>
        !string.IsNullOrWhiteSpace(code) && Labels.ContainsKey(code.Trim());

    public static string LabelFor(string code) =>
        Labels.TryGetValue(code.Trim(), out var label) ? label : code.Trim();

    // Composes the persisted/emailed reason: "<label>: <note>" — or just the label when no note.
    public static string Compose(string code, string? note)
    {
        var label = LabelFor(code);
        var trimmedNote = note?.Trim();
        return string.IsNullOrEmpty(trimmedNote) ? label : $"{label}: {trimmedNote}";
    }
}
