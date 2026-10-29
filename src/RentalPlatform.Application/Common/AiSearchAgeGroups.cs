namespace RentalPlatform.Application.Common;

// The catalogue's existing age-group keys, mirrored from the UI (Rental-Ui listings-page AGE_GROUPS and
// parseAgeGroupToMonths): "0-12", "12-36", "36-72", "72-120" and the open-ended "120+". Each key is a
// month range; the catalogue query takes it as AgeFromMonths/AgeToMonths. A parsed age in months maps
// to the group that contains it, lower bound inclusive (36 months -> "36-72").
public static class AiSearchAgeGroups
{
    public sealed record AgeGroup(string Key, int FromMonths, int? ToMonths);

    public static readonly IReadOnlyList<AgeGroup> All = new[]
    {
        new AgeGroup("0-12", 0, 12),
        new AgeGroup("12-36", 12, 36),
        new AgeGroup("36-72", 36, 72),
        new AgeGroup("72-120", 72, 120),
        new AgeGroup("120+", 120, null)
    };

    public static AgeGroup? FromMonths(int months)
    {
        if (months < 0)
        {
            return null;
        }

        return All.FirstOrDefault(group => months >= group.FromMonths && (group.ToMonths is null || months < group.ToMonths));
    }

    public static AgeGroup? FromKey(string? key) =>
        string.IsNullOrWhiteSpace(key) ? null : All.FirstOrDefault(group => group.Key == key.Trim());
}
