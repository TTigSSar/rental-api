using RentalPlatform.Domain.Entities;

namespace RentalPlatform.Application.Abstractions;

public interface IAiSearchLogStore
{
    /// <summary>Sum of CostUsd for rows created at or after <paramref name="sinceUtc"/>.</summary>
    Task<decimal> GetSpentSinceAsync(DateTime sinceUtc, CancellationToken cancellationToken = default);

    /// <summary>
    /// Number of the user's searches created at or after <paramref name="sinceUtc"/> that were paid for
    /// (CostUsd &gt; 0), whatever the outcome; free fallbacks (CostUsd = 0) do not count.
    /// </summary>
    Task<int> CountModelSearchesSinceAsync(Guid userId, DateTime sinceUtc, CancellationToken cancellationToken = default);

    Task AddAsync(AiSearchLog entry, CancellationToken cancellationToken = default);
}
