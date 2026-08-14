using System.ComponentModel.DataAnnotations;

namespace RentalPlatform.Application.DTOs;

/// <summary>New DisplayOrder is assigned by each id's position in this array. The set of ids must
/// exactly match the full set of existing categories — see admin.category_order_mismatch.</summary>
public sealed class ReorderCategoriesRequest
{
    [Required(ErrorMessage = "orderedIds is required.")]
    public IReadOnlyList<Guid> OrderedIds { get; init; } = Array.Empty<Guid>();
}
