using System.ComponentModel.DataAnnotations;

namespace RentalPlatform.Application.DTOs;

public sealed class UpdateListingCategoryRequest
{
    [Required(ErrorMessage = "Category is required.")]
    public Guid CategoryId { get; init; }
}
