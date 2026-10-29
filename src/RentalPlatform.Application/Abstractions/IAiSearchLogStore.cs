using RentalPlatform.Domain.Entities;

namespace RentalPlatform.Application.Abstractions;

public interface IAiSearchLogStore
{
    /// <summary>Sum of CostUsd for rows created at or after <paramref name="sinceUtc"/>.</summary>
    Task<decimal> GetSpentSinceAsync(DateTime sinceUtc, CancellationToken cancellationToken = default);

    Task AddAsync(AiSearchLog entry, CancellationToken cancellationToken = default);
}
