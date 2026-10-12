using Challenge.Api.Data;
using Challenge.Api.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Challenge.Api.Controllers;

[ApiController]
[Authorize(Roles = "Admin,Sales")]
[Route("api/reports")]
public class ReportsController : ControllerBase
{
    private readonly AppDbContext _db;

    public ReportsController(AppDbContext db)
    {
        _db = db;
    }

    public record TopProductResponse(string ProductCode, string ProductName, long QuantitySold, decimal TotalSales);

    [HttpGet("top-products")]
    public async Task<IActionResult> TopProducts(
        [FromQuery] DateOnly? fromDate = null,
        [FromQuery] DateOnly? toDate = null,
        CancellationToken cancellationToken = default)
    {
        if (fromDate.HasValue && toDate.HasValue && fromDate.Value > toDate.Value)
        {
            ModelState.AddModelError(nameof(toDate), "To date must be on or after from date.");
            return ValidationProblem();
        }

        var query = _db.OrderItems.AsNoTracking().Where(x => x.Order.Status == OrderStatus.Confirmed);
        if (fromDate.HasValue)
        {
            var start = fromDate.Value.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            query = query.Where(x => x.Order.OrderDate >= start);
        }

        if (toDate.HasValue && toDate.Value != DateOnly.MaxValue)
        {
            var end = toDate.Value.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            query = query.Where(x => x.Order.OrderDate < end);
        }

        var products = await query
            .GroupBy(x => new { x.ProductId, x.Product.Code, x.Product.Name })
            .Select(group => new
            {
                group.Key.ProductId,
                group.Key.Code,
                group.Key.Name,
                QuantitySold = group.Sum(x => (long)x.Quantity),
                TotalSales = group.Sum(x => x.TotalPrice)
            })
            .OrderByDescending(x => x.QuantitySold).ThenBy(x => x.Code).ThenBy(x => x.ProductId)
            .Select(x => new TopProductResponse(x.Code, x.Name, x.QuantitySold, x.TotalSales))
            .ToListAsync(cancellationToken);

        return Ok(products);
    }
}
