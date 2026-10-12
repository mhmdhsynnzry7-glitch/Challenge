using Challenge.Api.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Challenge.Api.Controllers;

[ApiController]
[Authorize(Roles = "Admin,Sales")]
[Route("api/products")]
public class ProductsController : ControllerBase
{
    private readonly AppDbContext _db;

    public ProductsController(AppDbContext db)
    {
        _db = db;
    }

    public record ProductResponse(int Id, string Code, string Name, decimal Price, int StockQuantity, bool IsActive);
    public record ProductListResponse(IReadOnlyList<ProductResponse> Items, int TotalCount, int Page, int PageSize);

    [HttpGet]
    public async Task<IActionResult> Get(
        [FromQuery] string? search,
        [FromQuery] bool? isActive,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10,
        CancellationToken cancellationToken = default)
    {
        if (page < 1)
        {
            ModelState.AddModelError(nameof(page), "Page must be greater than zero.");
        }

        if (pageSize < 1 || pageSize > 100)
        {
            ModelState.AddModelError(nameof(pageSize), "Page size must be between 1 and 100.");
        }

        if (!ModelState.IsValid)
        {
            return ValidationProblem();
        }

        var offset = (long)(page - 1) * pageSize;
        if (offset > int.MaxValue)
        {
            ModelState.AddModelError(nameof(page), "The requested page exceeds the supported pagination range.");
            return ValidationProblem();
        }

        var active = isActive ?? true;
        var query = _db.Products.AsNoTracking().Where(x => x.IsActive == active);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(x => x.Code.Contains(term) || x.Name.Contains(term));
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query.OrderBy(x => x.Code).ThenBy(x => x.Id)
            .Skip((int)offset).Take(pageSize)
            .Select(x => new ProductResponse(x.Id, x.Code, x.Name, x.Price, x.StockQuantity, x.IsActive))
            .ToListAsync(cancellationToken);

        return Ok(new ProductListResponse(items, totalCount, page, pageSize));
    }
}
