using RentalPlatform.Application.DTOs;

namespace RentalPlatform.Application.Abstractions;

public interface ICategoriesQueryService
{
    Task<IReadOnlyCollection<CategoryResponse>> GetAllAsync(CancellationToken cancellationToken = default);
}
