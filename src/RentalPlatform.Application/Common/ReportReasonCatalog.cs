using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Application.Common;

// Single source of truth for the structured community-report reasons. Codes are kept in sync
// with the Angular report dialog's reason picker. Unlike RejectionReasonCatalog (which only has
// labels), each reason here also carries a fixed Severity: severity is derived server-side from
// the reason code, never client-supplied — see ReportsService.CreateAsync.
public static class ReportReasonCatalog
{
    public const int MaxDetailLength = 2000;

    private sealed record ReasonInfo(string Label, ReportSeverity Severity);

    private static readonly IReadOnlyDictionary<string, ReasonInfo> Reasons =
        new Dictionary<string, ReasonInfo>(StringComparer.OrdinalIgnoreCase)
        {
            ["noShow"] = new("No-show at pickup", ReportSeverity.High),
            ["unsafeItem"] = new("Unsafe or broken item", ReportSeverity.High),
            ["misleadingPhotos"] = new("Misleading photos", ReportSeverity.Medium),
            ["offPlatformPayment"] = new("Off-platform payment attempt", ReportSeverity.Medium),
            ["rudeMessages"] = new("Rude or abusive messages", ReportSeverity.Low),
            ["spam"] = new("Spam or scam", ReportSeverity.Medium),
            ["other"] = new("Other", ReportSeverity.Low),
        };

    public static bool IsKnownCode(string? code) =>
        !string.IsNullOrWhiteSpace(code) && Reasons.ContainsKey(code.Trim());

    public static string LabelFor(string code) =>
        Reasons.TryGetValue(code.Trim(), out var info) ? info.Label : code.Trim();

    public static ReportSeverity SeverityFor(string code) =>
        Reasons.TryGetValue(code.Trim(), out var info) ? info.Severity : ReportSeverity.Low;

    /// <summary>
    /// Reason codes whose label contains <paramref name="term"/>, case-insensitive. The admin
    /// queue's search is specified to match "reason label" too, but the label isn't a persisted
    /// column EF Core could translate a dictionary lookup against — so the small, fixed catalog
    /// is matched here in memory, and the resulting codes are filtered against the ReasonCode
    /// column in the SQL query instead (see AdminReportsStore.ApplySearch).
    /// </summary>
    public static IReadOnlyCollection<string> CodesMatchingLabel(string term) =>
        Reasons
            .Where(pair => pair.Value.Label.Contains(term, StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Key)
            .ToList();
}
