namespace RentalPlatform.Application.Common;

// The model's ranking is advice, not truth (ADR-054): unknown ids are dropped, repeats are dropped,
// and any candidate the model forgot is appended in the original (fallback) order. The model can
// reorder the candidates, never add or remove one.
public static class AiSearchRankValidator
{
    public static IReadOnlyList<Guid> Merge(IReadOnlyList<Guid> fallbackOrder, IReadOnlyList<Guid>? modelOrder)
    {
        var known = new HashSet<Guid>(fallbackOrder);
        var result = new List<Guid>(fallbackOrder.Count);
        var seen = new HashSet<Guid>();

        if (modelOrder is not null)
        {
            foreach (var id in modelOrder)
            {
                if (known.Contains(id) && seen.Add(id))
                {
                    result.Add(id);
                }
            }
        }

        foreach (var id in fallbackOrder)
        {
            if (seen.Add(id))
            {
                result.Add(id);
            }
        }

        return result;
    }
}
