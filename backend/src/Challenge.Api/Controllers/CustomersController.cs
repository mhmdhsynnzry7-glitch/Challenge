using Challenge.Api.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Challenge.Api.Controllers;

[ApiController]
[Authorize(Roles = "Admin,Sales")]
[Route("api/customers")]
public class CustomersController : ControllerBase
{
    private readonly AppDbContext _db;

    public CustomersController(AppDbContext db)
    {
        _db = db;
    }

    public record CustomerResponse(int Id, string Code, string Name);

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var customers = await _db.Customers.AsNoTracking()
            .Where(x => x.IsActive)
            .OrderBy(x => x.Code).ThenBy(x => x.Id)
            .Select(x => new CustomerResponse(x.Id, x.Code, x.Name))
            .ToListAsync(cancellationToken);

        return Ok(customers);
    }
}
