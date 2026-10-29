using Microsoft.EntityFrameworkCore;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Domain.Entities;

namespace RentalPlatform.Infrastructure.Persistence;

public sealed class AiSearchLogStore : IAiSearchLogStore
{
    private readonly AppDbContext _dbContext;

    public AiSearchLogStore(AppDbContext dbContext) => _dbContext = dbContext;

    public async Task<decimal> GetSpentSinceAsync(DateTime sinceUtc, CancellationToken cancellationToken = default)
    {
        // Summed as double: SQLite (the test provider) cannot aggregate decimal in SQL, and a budget
        // compare at 1e-6 USD resolution does not need decimal exactness.
        var spent = await _dbContext.AiSearchLogs
            .AsNoTracking()
            .Where(log => log.CreatedAt >= sinceUtc)
            .SumAsync(log => (double)log.CostUsd, cancellationToken);

        return (decimal)spent;
    }

    public async Task AddAsync(AiSearchLog entry, CancellationToken cancellationToken = default)
    {
        _dbContext.AiSearchLogs.Add(entry);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
