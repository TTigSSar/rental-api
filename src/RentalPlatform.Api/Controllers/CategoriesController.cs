using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.DTOs;

namespace RentalPlatform.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[AllowAnonymous]
public sealed class CategoriesController : ControllerBase
{
    private readonly ICategoriesQueryService _categoriesQueryService;

    public CategoriesController(ICategoriesQueryService categoriesQueryService)
    {
        _categoriesQueryService = categoriesQueryService;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyCollection<CategoryResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyCollection<CategoryResponse>>> GetAll(CancellationToken cancellationToken)
    {
        var categories = await _categoriesQueryService.GetAllAsync(cancellationToken);
        return Ok(categories);
    }
}
