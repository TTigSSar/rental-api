using Microsoft.EntityFrameworkCore;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.DTOs;
using RentalPlatform.Domain.Entities;

namespace RentalPlatform.Infrastructure.Persistence;

public sealed class AdminDemandStore : IAdminDemandStore
{
    private readonly AppDbContext _dbContext;

    public AdminDemandStore(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<User?> FindUserByIdAsync(Guid userId, CancellationToken cancellationToken = default) =>
        _dbContext.Users.FirstOrDefaultAsync(user => user.Id == userId, cancellationToken);

    public async Task<IReadOnlyList<DemandRowResponse>> GetRowsAsync(
        DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        var rows = await _dbContext.DemandDailyCounts
            .AsNoTracking()
            .Where(count => count.Day >= from && count.Day <= to)
            .ToListAsync(cancellationToken);

        // Ordered in memory so "NULL district first" and the Guid order are the same on every provider
        // (SQL Server sorts uniqueidentifier in its own byte order, which differs from Guid.CompareTo).
        // The set is bounded: at most 366 days.
        return rows
            .OrderBy(count => count.Day)
            .ThenBy(count => count.DistrictId.HasValue)
            .ThenBy(count => count.DistrictId)
            .ThenBy(count => count.CategoryId)
            .Select(count => new DemandRowResponse
            {
                Day = count.Day,
                DistrictId = count.DistrictId,
                CategoryId = count.CategoryId,
                BookingRequests = count.BookingRequests
            })
            .ToList();
    }
}
