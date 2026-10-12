using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json.Serialization;
using Challenge.Api.Data;
using Challenge.Api.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Challenge.Api.Controllers;

[ApiController]
[Authorize(Roles = "Admin,Sales")]
[Route("api/orders")]
public class OrdersController : ControllerBase
{
    private const decimal MaximumAmount = 9_999_999_999_999_999.99m;
    private readonly AppDbContext _db;

    public OrdersController(AppDbContext db)
    {
        _db = db;
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public record CreateOrderRequest(
        [Range(1, int.MaxValue)] int CustomerId,
        [Required, MinLength(1)] List<CreateOrderItemRequest?>? Items);

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public record CreateOrderItemRequest(
        [Range(1, int.MaxValue)] int ProductId,
        [Range(1, int.MaxValue)] int Quantity);

    public record OrderItemResponse(int ProductId, int Quantity, decimal UnitPrice, decimal TotalPrice);
    public record CreateOrderResponse(int Id, string OrderNumber, int CustomerId, DateTime OrderDate,
        string Status, decimal TotalAmount, int CreatedBy, IReadOnlyList<OrderItemResponse> Items);

    public record OrderListItemResponse(int Id, string OrderNumber, int CustomerId, string CustomerName,
        DateTime OrderDate, string Status, decimal TotalAmount);
    public record OrderListResponse(IReadOnlyList<OrderListItemResponse> Items, int TotalCount, int Page, int PageSize);
    public record OrderCreatorResponse(int Id, string Username);
    public record OrderItemDetailsResponse(int ProductId, int Quantity, decimal UnitPrice, decimal TotalPrice,
        string ProductCode, string ProductName) : OrderItemResponse(ProductId, Quantity, UnitPrice, TotalPrice);
    public record OrderDetailsResponse(int Id, string OrderNumber, int CustomerId, DateTime OrderDate,
        string Status, decimal TotalAmount, int CreatedBy, CustomersController.CustomerResponse Customer,
        OrderCreatorResponse CreatedByUser, IReadOnlyList<OrderItemDetailsResponse> Items);

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateOrderRequest request, CancellationToken cancellationToken)
    {
        var requestedItems = request.Items;
        if (requestedItems is null || requestedItems.Count == 0)
        {
            return OrderError(400, "empty_order", "An order must contain at least one item.");
        }

        if (requestedItems.Any(x => x is null))
        {
            return OrderError(400, "invalid_order_item", "Order items must not be null.");
        }

        var items = requestedItems.Select(x => x!).OrderBy(x => x.ProductId).ToList();
        if (items.Select(x => x.ProductId).Distinct().Count() != items.Count)
        {
            return OrderError(400, "duplicate_product", "Each product may appear only once in an order.");
        }

        if (!int.TryParse(User.FindFirstValue(JwtRegisteredClaimNames.Sub), NumberStyles.None,
                CultureInfo.InvariantCulture, out var createdBy) || createdBy <= 0 ||
            !await _db.Users.AsNoTracking().AnyAsync(x => x.Id == createdBy && x.IsActive, cancellationToken))
        {
            return OrderError(401, "invalid_user", "A valid active user is required.");
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted,
            cancellationToken);
        var customer = await _db.Customers.AsNoTracking()
            .Where(x => x.Id == request.CustomerId)
            .Select(x => new { x.IsActive })
            .SingleOrDefaultAsync(cancellationToken);
        if (customer is null)
        {
            return OrderError(404, "customer_not_found", "Customer was not found.");
        }

        if (!customer.IsActive)
        {
            return OrderError(400, "inactive_customer", "Customer is inactive.");
        }

        var productIds = items.Select(x => x.ProductId).ToArray();
        var products = await _db.Products.AsNoTracking().Where(x => productIds.Contains(x.Id))
            .Select(x => new { x.Id, x.IsActive }).ToDictionaryAsync(x => x.Id, cancellationToken);
        foreach (var item in items)
        {
            if (!products.TryGetValue(item.ProductId, out var product))
            {
                return OrderError(404, "product_not_found", $"Product {item.ProductId} was not found.");
            }

            if (!product.IsActive)
            {
                return OrderError(400, "inactive_product", $"Product {item.ProductId} is inactive.");
            }
        }

        var now = DateTime.UtcNow;
        var order = new Order
        {
            OrderNumber = Guid.NewGuid().ToString("N"),
            CustomerId = request.CustomerId,
            OrderDate = now,
            Status = OrderStatus.Confirmed,
            CreatedBy = createdBy,
            CreatedAt = now,
            UpdatedAt = now
        };

        foreach (var item in items)
        {
            var affected = await _db.Products
                .Where(x => x.Id == item.ProductId && x.IsActive && x.StockQuantity >= item.Quantity)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.StockQuantity, x => x.StockQuantity - item.Quantity)
                    .SetProperty(x => x.UpdatedAt, now), cancellationToken);
            if (affected != 1)
            {
                return OrderError(409, "insufficient_stock",
                    $"Product {item.ProductId} is unavailable or has insufficient stock.");
            }

            // The successful update holds the product's write lock until this transaction completes.
            var price = await _db.Products.AsNoTracking().Where(x => x.Id == item.ProductId)
                .Select(x => x.Price).SingleAsync(cancellationToken);
            var totalPrice = price * item.Quantity;
            if (totalPrice > MaximumAmount || order.TotalAmount + totalPrice > MaximumAmount)
            {
                return OrderError(400, "amount_out_of_range", "Order amounts exceed the supported monetary range.");
            }

            order.Items.Add(new OrderItem
            {
                ProductId = item.ProductId,
                Quantity = item.Quantity,
                UnitPrice = price,
                TotalPrice = totalPrice
            });
            order.TotalAmount += totalPrice;
        }

        _db.Orders.Add(order);
        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return CreatedAtAction(nameof(GetById), new { id = order.Id }, new CreateOrderResponse(
            order.Id, order.OrderNumber, order.CustomerId, order.OrderDate, order.Status.ToString(),
            order.TotalAmount, order.CreatedBy,
            order.Items.Select(x => new OrderItemResponse(x.ProductId, x.Quantity, x.UnitPrice, x.TotalPrice)).ToList()));
    }

    private ObjectResult OrderError(int status, string code, string message) => Problem(
        statusCode: status, title: message,
        extensions: new Dictionary<string, object?> { ["code"] = code });

    [HttpGet]
    public async Task<IActionResult> GetList(
        [FromQuery] string? orderNumber = null,
        [FromQuery] int? customerId = null,
        [FromQuery] OrderStatus? status = null,
        [FromQuery] DateOnly? fromDate = null,
        [FromQuery] DateOnly? toDate = null,
        [FromQuery] string? sortBy = "orderDate",
        [FromQuery] string? sortDirection = "desc",
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10,
        CancellationToken cancellationToken = default)
    {
        var sort = sortBy?.Trim().ToLowerInvariant() ?? "orderdate";
        var direction = sortDirection?.Trim().ToLowerInvariant() ?? "desc";
        if (customerId.HasValue && customerId.Value < 1)
        {
            ModelState.AddModelError(nameof(customerId), "Customer ID must be greater than zero.");
        }

        if (status.HasValue && !Enum.IsDefined(status.Value))
        {
            ModelState.AddModelError(nameof(status), "Status must be Confirmed or Cancelled.");
        }

        if (fromDate.HasValue && toDate.HasValue && fromDate.Value > toDate.Value)
        {
            ModelState.AddModelError(nameof(toDate), "To date must be on or after from date.");
        }

        if (sort is not ("orderdate" or "ordernumber" or "totalamount"))
        {
            ModelState.AddModelError(nameof(sortBy), "Sort by must be orderDate, orderNumber, or totalAmount.");
        }

        if (direction is not ("asc" or "desc"))
        {
            ModelState.AddModelError(nameof(sortDirection), "Sort direction must be asc or desc.");
        }

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

        var query = _db.Orders.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(orderNumber))
        {
            var term = orderNumber.Trim();
            query = query.Where(x => x.OrderNumber.Contains(term));
        }

        if (customerId.HasValue)
        {
            query = query.Where(x => x.CustomerId == customerId.Value);
        }

        if (status.HasValue)
        {
            query = query.Where(x => x.Status == status.Value);
        }

        if (fromDate.HasValue)
        {
            var start = fromDate.Value.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            query = query.Where(x => x.OrderDate >= start);
        }

        if (toDate.HasValue && toDate.Value != DateOnly.MaxValue)
        {
            var end = toDate.Value.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            query = query.Where(x => x.OrderDate < end);
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var ordered = (sort, direction) switch
        {
            ("ordernumber", "asc") => query.OrderBy(x => x.OrderNumber).ThenBy(x => x.Id),
            ("ordernumber", "desc") => query.OrderByDescending(x => x.OrderNumber).ThenByDescending(x => x.Id),
            ("totalamount", "asc") => query.OrderBy(x => x.TotalAmount).ThenBy(x => x.Id),
            ("totalamount", "desc") => query.OrderByDescending(x => x.TotalAmount).ThenByDescending(x => x.Id),
            ("orderdate", "asc") => query.OrderBy(x => x.OrderDate).ThenBy(x => x.Id),
            _ => query.OrderByDescending(x => x.OrderDate).ThenByDescending(x => x.Id)
        };
        var items = await ordered.Skip((int)offset).Take(pageSize)
            .Select(x => new OrderListItemResponse(x.Id, x.OrderNumber, x.CustomerId, x.Customer.Name,
                DateTime.SpecifyKind(x.OrderDate, DateTimeKind.Utc), x.Status.ToString(), x.TotalAmount))
            .ToListAsync(cancellationToken);

        return Ok(new OrderListResponse(items, totalCount, page, pageSize));
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id, CancellationToken cancellationToken)
    {
        if (id < 1)
        {
            return OrderError(400, "invalid_order_id", "Order ID must be greater than zero.");
        }

        var order = await _db.Orders.AsNoTracking().Where(x => x.Id == id)
            .Select(x => new OrderDetailsResponse(x.Id, x.OrderNumber, x.CustomerId,
                DateTime.SpecifyKind(x.OrderDate, DateTimeKind.Utc), x.Status.ToString(), x.TotalAmount, x.CreatedBy,
                new CustomersController.CustomerResponse(x.Customer.Id, x.Customer.Code, x.Customer.Name),
                new OrderCreatorResponse(x.CreatedByUser.Id, x.CreatedByUser.Username),
                x.Items.OrderBy(i => i.ProductId).ThenBy(i => i.Id)
                    .Select(i => new OrderItemDetailsResponse(i.ProductId, i.Quantity, i.UnitPrice, i.TotalPrice,
                        i.Product.Code, i.Product.Name)).ToList()))
            .SingleOrDefaultAsync(cancellationToken);

        return order is null
            ? OrderError(404, "order_not_found", "Order was not found.")
            : Ok(order);
    }
}
