namespace RentalPlatform.Application.Common;

// The intent line is model output shown to the user, so it is checked (ADR-054 amendment): it must say
// what was looked for by intent only. A line with a digit (age, price, date) or any district name in
// en/ru/hy is dropped rather than shown.
public static class AiSearchIntentValidator
{
    public static string? Validate(string? intent, IEnumerable<string> districtNames)
    {
        if (string.IsNullOrWhiteSpace(intent))
        {
            return null;
        }

        var line = intent.Trim();
        if (line.Any(char.IsDigit))
        {
            return null;
        }

        foreach (var name in districtNames)
        {
            if (!string.IsNullOrWhiteSpace(name) && line.Contains(name.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        return line;
    }
}
