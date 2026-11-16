using System.ComponentModel.DataAnnotations;

namespace RentalPlatform.Application.DTOs;

public sealed class CreateCategoryRequest
{
    [Required(ErrorMessage = "Category name is required.")]
    [MaxLength(120, ErrorMessage = "Category name must be 120 characters or fewer.")]
    public string Name { get; init; } = string.Empty;

    [Required(ErrorMessage = "Armenian category name is required.")]
    [MaxLength(120, ErrorMessage = "Armenian category name must be 120 characters or fewer.")]
    public string NameHy { get; init; } = string.Empty;

    [Required(ErrorMessage = "Russian category name is required.")]
    [MaxLength(120, ErrorMessage = "Russian category name must be 120 characters or fewer.")]
    public string NameRu { get; init; } = string.Empty;

    [MaxLength(80, ErrorMessage = "Icon name must be 80 characters or fewer.")]
    public string? IconName { get; init; }

    [MaxLength(9, ErrorMessage = "Colour must be #RRGGBB or #RRGGBBAA.")]
    public string? ColorHex { get; init; }
}
