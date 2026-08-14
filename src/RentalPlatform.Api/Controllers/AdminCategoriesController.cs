using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RentalPlatform.Api.Extensions;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.Common;
using RentalPlatform.Application.DTOs;
using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Api.Controllers;

// Admin console Phase 2: the Categories screen. Slug is never regenerated on rename — see
// AdminCategoriesService.UpdateAsync's comment; HomeSectionsQueryService resolves two home-page
// carousels by hardcoded category slug ("baby-toys", "outdoor-toys"), and the admin/renter
// frontends key their icon/gradient visual lookup off category.slug, so a slug rewrite on rename
// would silently break both.
[ApiController]
[Route("api/admin/categories")]
[Authorize(Roles = nameof(UserRole.Admin))]
public sealed class AdminCategoriesController : ControllerBase
{
    private readonly IAdminCategoriesService _adminCategoriesService;

    public AdminCategoriesController(IAdminCategoriesService adminCategoriesService)
    {
        _adminCategoriesService = adminCategoriesService;
    }

    [HttpGet]
    [ProducesResponseType(typeof(AdminCategoriesResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<AdminCategoriesResponse>> GetAll(CancellationToken cancellationToken)
    {
        var result = await _adminCategoriesService.GetAllAsync(cancellationToken);
        if (result.IsSuccess && result.Value is not null)
        {
            return Ok(result.Value);
        }

        return FromError(result.Error);
    }

    [HttpPost]
    [ProducesResponseType(typeof(AdminCategoryResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AdminCategoryResponse>> Create(
        [FromBody] CreateCategoryRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _adminCategoriesService.CreateAsync(request, cancellationToken);
        if (result.IsSuccess && result.Value is not null)
        {
            // No per-category GET endpoint exists yet (only the collection GET), so this returns
            // 201 with the created resource in the body rather than a Location header pointing at
            // a route that doesn't resolve a single category.
            return StatusCode(StatusCodes.Status201Created, result.Value);
        }

        return FromError(result.Error);
    }

    [HttpPatch("{id:guid}")]
    [ProducesResponseType(typeof(AdminCategoryResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AdminCategoryResponse>> Update(
        Guid id,
        [FromBody] UpdateCategoryRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _adminCategoriesService.UpdateAsync(id, request, cancellationToken);
        if (result.IsSuccess && result.Value is not null)
        {
            return Ok(result.Value);
        }

        return FromError(result.Error);
    }

    [HttpPatch("{id:guid}/visibility")]
    [ProducesResponseType(typeof(AdminCategoryResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AdminCategoryResponse>> UpdateVisibility(
        Guid id,
        [FromBody] UpdateCategoryVisibilityRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _adminCategoriesService.UpdateVisibilityAsync(id, request.IsVisible, cancellationToken);
        if (result.IsSuccess && result.Value is not null)
        {
            return Ok(result.Value);
        }

        return FromError(result.Error);
    }

    [HttpPatch("reorder")]
    [ProducesResponseType(typeof(IReadOnlyCollection<AdminCategoryResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyCollection<AdminCategoryResponse>>> Reorder(
        [FromBody] ReorderCategoriesRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _adminCategoriesService.ReorderAsync(request.OrderedIds, cancellationToken);
        if (result.IsSuccess && result.Value is not null)
        {
            return Ok(result.Value);
        }

        return FromError(result.Error);
    }

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult> Delete(
        Guid id,
        [FromQuery] Guid? reassignToCategoryId,
        CancellationToken cancellationToken)
    {
        var result = await _adminCategoriesService.DeleteAsync(id, reassignToCategoryId, cancellationToken);
        if (result.IsSuccess)
        {
            return NoContent();
        }

        return FromError(result.Error);
    }

    private ActionResult FromError(ServiceError? error)
    {
        if (error is null)
        {
            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "Unexpected error.");
        }

        return error.Code switch
        {
            "admin.unauthenticated" => Unauthorized(error.ToProblemDetails(StatusCodes.Status401Unauthorized)),
            "admin.forbidden" => StatusCode(StatusCodes.Status403Forbidden, error.ToProblemDetails(StatusCodes.Status403Forbidden)),
            // NotFound: this code is now reserved for the ROUTE id ({id:guid}) not resolving —
            // PATCH/PATCH-visibility/DELETE all use it that way. The reassignToCategoryId query
            // parameter not resolving is a distinct code below (admin.category_reassign_target_not_found,
            // 400) — a bad request payload rather than a missing resource. AdminListingsController's
            // own request-body category reference (the recategorise target) uses that same 400 code,
            // so admin.category_not_found means exactly one thing — "route id not found" — everywhere.
            "admin.category_not_found" => NotFound(error.ToProblemDetails(StatusCodes.Status404NotFound)),
            "admin.category_reassign_target_not_found" => BadRequest(error.ToProblemDetails(StatusCodes.Status400BadRequest)),
            "admin.category_name_taken" => Conflict(error.ToProblemDetails(StatusCodes.Status409Conflict)),
            "admin.category_invalid_name" => BadRequest(error.ToProblemDetails(StatusCodes.Status400BadRequest)),
            "admin.category_invalid_color" => BadRequest(error.ToProblemDetails(StatusCodes.Status400BadRequest)),
            "admin.category_order_mismatch" => BadRequest(error.ToProblemDetails(StatusCodes.Status400BadRequest)),
            "admin.category_reassign_required" => BadRequest(error.ToProblemDetails(StatusCodes.Status400BadRequest)),
            "admin.category_reassign_invalid" => BadRequest(error.ToProblemDetails(StatusCodes.Status400BadRequest)),
            _ => BadRequest(error.ToProblemDetails(StatusCodes.Status400BadRequest))
        };
    }
}
