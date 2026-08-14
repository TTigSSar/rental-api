using RentalPlatform.Application.Common;
using RentalPlatform.Application.DTOs;

namespace RentalPlatform.Application.Abstractions;

public interface IAdminCategoriesService
{
    Task<ServiceResult<AdminCategoriesResponse>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<ServiceResult<AdminCategoryResponse>> CreateAsync(
        CreateCategoryRequest request, CancellationToken cancellationToken = default);

    Task<ServiceResult<AdminCategoryResponse>> UpdateAsync(
        Guid categoryId, UpdateCategoryRequest request, CancellationToken cancellationToken = default);

    Task<ServiceResult<AdminCategoryResponse>> UpdateVisibilityAsync(
        Guid categoryId, bool isVisible, CancellationToken cancellationToken = default);

    Task<ServiceResult<IReadOnlyCollection<AdminCategoryResponse>>> ReorderAsync(
        IReadOnlyList<Guid> orderedIds, CancellationToken cancellationToken = default);

    Task<ServiceResult<bool>> DeleteAsync(
        Guid categoryId, Guid? reassignToCategoryId, CancellationToken cancellationToken = default);
}
