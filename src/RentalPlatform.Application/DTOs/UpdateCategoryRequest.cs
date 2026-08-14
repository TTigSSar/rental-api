using System.ComponentModel.DataAnnotations;

namespace RentalPlatform.Application.DTOs;

/// <summary>Rename/restyle a category. All fields optional — only supplied fields are changed.
/// Slug is never regenerated here; see AdminCategoriesService for why.</summary>
public sealed class UpdateCategoryRequest
{
    [MaxLength(120, ErrorMessage = "Category name must be 120 characters or fewer.")]
    public string? Name { get; init; }

    [MaxLength(80, ErrorMessage = "Icon name must be 80 characters or fewer.")]
    public string? IconName { get; init; }

    [MaxLength(9, ErrorMessage = "Colour must be #RRGGBB or #RRGGBBAA.")]
    public string? ColorHex { get; init; }
}
