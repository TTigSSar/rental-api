using System.ComponentModel.DataAnnotations;
using RentalPlatform.Application.Common;

namespace RentalPlatform.Application.DTOs;

public sealed class RejectListingRequest : IValidatableObject
{
    [Required(ErrorMessage = "Rejection reason is required.")]
    public string ReasonCode { get; init; } = string.Empty;

    [StringLength(RejectionReasonCatalog.MaxReasonLength, ErrorMessage = "Rejection note must be 1000 characters or fewer.")]
    public string? Note { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!RejectionReasonCatalog.IsKnownCode(ReasonCode))
        {
            yield return new ValidationResult(
                "Rejection reason is invalid.",
                new[] { nameof(ReasonCode) });
        }
        else if (RejectionReasonCatalog.Compose(ReasonCode, Note).Length > RejectionReasonCatalog.MaxReasonLength)
        {
            yield return new ValidationResult(
                "Rejection reason must be between 1 and 1000 characters.",
                new[] { nameof(Note) });
        }
    }
}
