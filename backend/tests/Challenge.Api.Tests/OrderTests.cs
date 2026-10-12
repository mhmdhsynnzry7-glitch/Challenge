using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Challenge.Api.Auth;
using Challenge.Api.Data;
using Challenge.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Xunit.Abstractions;
using CreateOrderResponse = Challenge.Api.Controllers.OrdersController.CreateOrderResponse;

namespace Challenge.Api.Tests;

[Collection("Order database mutations")]
public class OrderTests : IClassFixture<OrdersApiFactory>, IAsyncLifetime
{
    private readonly OrdersApiFactory _factory;
    private readonly ITestOutputHelper _output;
    private readonly HttpClient _client;
    private readonly string _code = "O4-" + Guid.NewGuid().ToString("N")[..16];
    private Customer _customer = null!;
    private Product _first = null!;
    private Product _second = null!;
    private Product _inactive = null!;
    private int _actorId;
    private readonly List<(int Id, int Stock, DateTime UpdatedAt)> _restoreProducts = new();

    public OrderTests(OrdersApiFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
        _client = factory.CreateClient();
    }

    public async Task InitializeAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;
        _customer = new Customer
        {
            Code = _code, Name = "Order test customer", PhoneNumber = "09120000000",
            IsActive = true, CreatedAt = now, UpdatedAt = now
        };
        _first = Product(_code + "-A", 12.34m, true);
        _second = Product(_code + "-B", 5.67m, true);
        _inactive = Product(_code + "-I", 3m, false);
        db.Customers.Add(_customer);
        db.Products.AddRange(_first, _second, _inactive);
        await db.SaveChangesAsync();
        await AuthenticateAsync();
    }

    public async Task DisposeAsync()
    {
        _factory.InventoryGate = null;
        _factory.FailAfterOrderSave = false;
        try
        {
            await using var scope = _factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync();
            // Only orders for this test-owned customer are removed; items cascade through the existing FK.
            await db.Orders.Where(x => x.CustomerId == _customer.Id && x.Customer.Code == _code)
                .ExecuteDeleteAsync();
            foreach (var snapshot in _restoreProducts)
            {
                await db.Products.Where(x => x.Id == snapshot.Id).ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.StockQuantity, snapshot.Stock)
                    .SetProperty(x => x.UpdatedAt, snapshot.UpdatedAt));
            }

            await db.Products.Where(x => x.Code == _code + "-A" || x.Code == _code + "-B" || x.Code == _code + "-I")
                .ExecuteDeleteAsync();
            await db.Customers.Where(x => x.Id == _customer.Id && x.Code == _code).ExecuteDeleteAsync();
            await transaction.CommitAsync();
        }
        finally
        {
            _client.Dispose();
        }
    }

    [Theory]
    [InlineData("admin", "Admin123!")]
    [InlineData("sales", "Sales123!")]
    public async Task Create_ValidOrderPersistsCustomerJwtActorAndServerValues(string username, string password)
    {
        await AuthenticateAsync(username, password);
        using var response = await CreateAsync((_first.Id, 2));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<CreateOrderResponse>())!;
        Assert.Equal(_customer.Id, result.CustomerId);
        Assert.Equal(_actorId, result.CreatedBy);
        Assert.Equal("Confirmed", result.Status);
        Assert.True(Guid.TryParseExact(result.OrderNumber, "N", out _));
        Assert.Equal(DateTimeKind.Utc, result.OrderDate.Kind);
        Assert.EndsWith("/api/orders/" + result.Id, response.Headers.Location!.ToString());
        Assert.Equal(24.68m, result.TotalAmount);
        var line = Assert.Single(result.Items);
        Assert.Equal(_first.Id, line.ProductId);
        Assert.Equal(2, line.Quantity);
        Assert.Equal(12.34m, line.UnitPrice);
        Assert.Equal(24.68m, line.TotalPrice);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var order = await db.Orders.AsNoTracking().Include(x => x.Items).SingleAsync(x => x.Id == result.Id);
        Assert.Equal(_customer.Id, order.CustomerId);
        Assert.Equal(_actorId, order.CreatedBy);
        Assert.Equal(OrderStatus.Confirmed, order.Status);
        Assert.Equal(result.OrderNumber, order.OrderNumber);
        Assert.Equal(24.68m, order.TotalAmount);
        Assert.Equal(24.68m, Assert.Single(order.Items).TotalPrice);
        Assert.Equal(8, await db.Products.Where(x => x.Id == _first.Id).Select(x => x.StockQuantity).SingleAsync());
    }

    [Fact]
    public async Task Create_MultipleProductsCalculatesDecimalTotalsAndDecrementsEveryStock()
    {
        using var response = await CreateAsync((_second.Id, 3), (_first.Id, 2));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<CreateOrderResponse>())!;
        Assert.Equal(41.69m, result.TotalAmount);
        Assert.Equal(new[] { _first.Id, _second.Id }, result.Items.Select(x => x.ProductId));
        Assert.Equal(new[] { 24.68m, 17.01m }, result.Items.Select(x => x.TotalPrice));
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(8, await StockAsync(db, _first.Id));
        Assert.Equal(7, await StockAsync(db, _second.Id));
        Assert.Equal(2, await db.OrderItems.CountAsync(x => x.OrderId == result.Id));
    }

    [Theory]
    [InlineData("null-request")]
    [InlineData("malformed")]
    [InlineData("empty-body")]
    [InlineData("missing-items")]
    [InlineData("null-items")]
    [InlineData("empty-items")]
    [InlineData("null-item")]
    [InlineData("missing-customer")]
    [InlineData("zero-customer")]
    [InlineData("missing-product")]
    [InlineData("zero-product")]
    [InlineData("missing-quantity")]
    [InlineData("malformed-quantity")]
    public async Task Create_InvalidRequestReturnsBadRequestWithoutChangingData(string variant)
    {
        var normalItem = new { productId = _first.Id, quantity = 1 };
        var json = variant switch
        {
            "null-request" => "null",
            "malformed" => "{",
            "empty-body" => "",
            "missing-items" => JsonSerializer.Serialize(new { customerId = _customer.Id }),
            "null-items" => JsonSerializer.Serialize(new { customerId = _customer.Id, items = (object?)null }),
            "empty-items" => JsonSerializer.Serialize(new { customerId = _customer.Id, items = Array.Empty<object>() }),
            "null-item" => JsonSerializer.Serialize(new { customerId = _customer.Id, items = new object?[] { null } }),
            "missing-customer" => JsonSerializer.Serialize(new { items = new[] { normalItem } }),
            "zero-customer" => JsonSerializer.Serialize(new { customerId = 0, items = new[] { normalItem } }),
            "missing-product" => JsonSerializer.Serialize(new { customerId = _customer.Id, items = new[] { new { quantity = 1 } } }),
            "zero-product" => JsonSerializer.Serialize(new { customerId = _customer.Id, items = new[] { new { productId = 0, quantity = 1 } } }),
            "missing-quantity" => JsonSerializer.Serialize(new { customerId = _customer.Id, items = new[] { new { productId = _first.Id } } }),
            _ => JsonSerializer.Serialize(new { customerId = _customer.Id, items = new[] { new { productId = _first.Id, quantity = "invalid" } } })
        };
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await _client.PostAsync("/api/orders", content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertNoOrderAsync();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public async Task Create_NonpositiveQuantityReturnsBadRequest(int quantity)
    {
        using var response = await CreateAsync((_first.Id, quantity));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertNoOrderAsync();
    }

    [Fact]
    public async Task Create_MissingCustomerReturnsNotFound()
    {
        using var response = await _client.PostAsJsonAsync("/api/orders",
            new { customerId = int.MaxValue, items = new[] { new { productId = _first.Id, quantity = 1 } } });
        await AssertErrorAsync(response, HttpStatusCode.NotFound, "customer_not_found");
        await AssertNoOrderAsync();
    }

    [Fact]
    public async Task Create_SeededInactiveCustomerReturnsBadRequest()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var id = await db.Customers.Where(x => x.Code == "CUST-005").Select(x => x.Id).SingleAsync();
        using var response = await _client.PostAsJsonAsync("/api/orders",
            new { customerId = id, items = new[] { new { productId = _first.Id, quantity = 1 } } });
        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "inactive_customer");
        await AssertNoOrderAsync();
    }

    [Fact]
    public async Task Create_MissingProductReturnsNotFound()
    {
        using var response = await CreateAsync((int.MaxValue, 1));
        await AssertErrorAsync(response, HttpStatusCode.NotFound, "product_not_found");
        await AssertNoOrderAsync();
    }

    [Fact]
    public async Task Create_InactiveProductReturnsBadRequest()
    {
        using var response = await CreateAsync((_inactive.Id, 1));
        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "inactive_product");
        await AssertNoOrderAsync();
    }

    [Fact]
    public async Task Create_InsufficientStockReturnsConflict()
    {
        using var response = await CreateAsync((_first.Id, 11));
        await AssertErrorAsync(response, HttpStatusCode.Conflict, "insufficient_stock");
        await AssertNoOrderAsync();
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(4, 7)]
    public async Task Create_DuplicateProductsAreRejectedWithoutConsumingStock(int firstQuantity, int secondQuantity)
    {
        using var response = await CreateAsync((_first.Id, firstQuantity), (_first.Id, secondQuantity));
        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "duplicate_product");
        await AssertNoOrderAsync();
    }

    [Theory]
    [InlineData("order", "totalAmount")]
    [InlineData("order", "status")]
    [InlineData("order", "orderNumber")]
    [InlineData("order", "createdBy")]
    [InlineData("order", "orderDate")]
    [InlineData("item", "unitPrice")]
    [InlineData("item", "totalPrice")]
    public async Task Create_ServerControlledFieldsAreRejected(string level, string field)
    {
        var item = new Dictionary<string, object> { ["productId"] = _first.Id, ["quantity"] = 1 };
        var request = new Dictionary<string, object> { ["customerId"] = _customer.Id, ["items"] = new[] { item } };
        (level == "order" ? request : item)[field] = "client-controlled";
        using var response = await _client.PostAsJsonAsync("/api/orders", request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertNoOrderAsync();
    }

    [Fact]
    public async Task Create_StoresHistoricalPricesWhenProductPriceChangesLater()
    {
        using var response = await CreateAsync((_first.Id, 2));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<CreateOrderResponse>())!;
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Products.Where(x => x.Id == _first.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Price, 99.99m));
        var item = await db.OrderItems.AsNoTracking().SingleAsync(x => x.OrderId == result.Id);
        Assert.Equal(12.34m, item.UnitPrice);
        Assert.Equal(24.68m, item.TotalPrice);
        Assert.Equal(24.68m, await db.Orders.Where(x => x.Id == result.Id).Select(x => x.TotalAmount).SingleAsync());
    }

    [Fact]
    public async Task Create_FailureOfLaterItemRollsBackEarlierStockAndTimestamp()
    {
        Assert.True(_first.Id < _second.Id);
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Products.Where(x => x.Id == _second.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.StockQuantity, 1));
        // Submit reverse order: the implementation must still update the lower product ID first.
        using var response = await CreateAsync((_second.Id, 3), (_first.Id, 4));
        await AssertErrorAsync(response, HttpStatusCode.Conflict, "insufficient_stock");
        await AssertNoOrderAsync(secondStock: 1);
        Assert.Equal(_first.UpdatedAt, await db.Products.Where(x => x.Id == _first.Id).Select(x => x.UpdatedAt).SingleAsync());
    }

    [Fact]
    public async Task Create_FailureAfterSavingOrderAndItemsRollsBackEveryOperation()
    {
        _factory.FailAfterOrderSave = true;
        try
        {
            using var response = await CreateAsync((_first.Id, 2), (_second.Id, 3));
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.Equal("{\"message\":\"An unexpected error occurred.\"}", await response.Content.ReadAsStringAsync());
        }
        finally
        {
            _factory.FailAfterOrderSave = false;
        }

        await AssertNoOrderAsync();
    }

    [Theory]
    [InlineData("line")]
    [InlineData("order")]
    public async Task Create_AmountOutsideMappedPrecisionReturnsBadRequestAndRollsBackStock(string variant)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ids = new[] { _first.Id, _second.Id };
        var price = variant == "line" ? 9_999_999_999_999_999.99m : 6_000_000_000_000_000m;
        await db.Products.Where(x => ids.Contains(x.Id)).ExecuteUpdateAsync(s => s.SetProperty(x => x.Price, price));
        using var response = variant == "line"
            ? await CreateAsync((_first.Id, 2))
            : await CreateAsync((_first.Id, 1), (_second.Id, 1));
        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "amount_out_of_range");
        await AssertNoOrderAsync();
    }

    [Fact]
    public async Task Create_ZeroPriceIsAllowedByTheExistingModel()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Products.Where(x => x.Id == _first.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Price, 0m));
        using var response = await CreateAsync((_first.Id, 1));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<CreateOrderResponse>())!;
        Assert.Equal(0m, result.TotalAmount);
        Assert.Equal(0m, Assert.Single(result.Items).UnitPrice);
        Assert.Equal(9, await StockAsync(db, _first.Id));
    }

    [Fact]
    public async Task Create_WithoutTokenReturnsUnauthorized()
    {
        _client.DefaultRequestHeaders.Authorization = null;
        using var response = await CreateAsync((_first.Id, 1));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertNoOrderAsync();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Unknown")]
    public async Task Create_WithoutPermittedRoleReturnsForbidden(string? role)
    {
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            Token(_actorId.ToString(CultureInfo.InvariantCulture), role));
        using var response = await CreateAsync((_first.Id, 1));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertNoOrderAsync();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-an-id")]
    [InlineData("2147483647")]
    public async Task Create_WithoutValidUserIdentityReturnsUnauthorized(string? subject)
    {
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(subject, "Sales"));
        using var response = await CreateAsync((_first.Id, 1));
        await AssertErrorAsync(response, HttpStatusCode.Unauthorized, "invalid_user");
        await AssertNoOrderAsync();
    }

    [Fact]
    public async Task Create_ConcurrentLaptopOrdersPersistExactlyOneAndNeverOversell()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var laptop = await db.Products.AsNoTracking().SingleAsync(x => x.Code == "LAPTOP-001");
        _restoreProducts.Add((laptop.Id, laptop.StockQuantity, laptop.UpdatedAt));
        await db.Products.Where(x => x.Id == laptop.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.StockQuantity, 5));
        var priorOrders = await db.Orders.CountAsync();
        var priorItems = await db.OrderItems.CountAsync();
        var gate = new OrdersApiFactory.StockUpdateGate();
        _factory.InventoryGate = gate;
        try
        {
            var requestA = CreateAsync((laptop.Id, 4));
            var requestB = CreateAsync((laptop.Id, 3));
            var responses = await Task.WhenAll(requestA, requestB);
            using var a = responses[0];
            using var b = responses[1];
            Assert.Equal(2, gate.Arrivals);
            Assert.Equal(2, gate.ConnectionIds.Count);
            Assert.DoesNotContain(Guid.Empty, gate.ConnectionIds.Keys);
            Assert.Equal(new[] { HttpStatusCode.Created, HttpStatusCode.Conflict },
                responses.Select(x => x.StatusCode).OrderBy(x => (int)x));
            var winner = a.StatusCode == HttpStatusCode.Created ? a : b;
            var loser = a.StatusCode == HttpStatusCode.Conflict ? a : b;
            await AssertErrorAsync(loser, HttpStatusCode.Conflict, "insufficient_stock");
            var quantity = a.StatusCode == HttpStatusCode.Created ? 4 : 3;
            var result = (await winner.Content.ReadFromJsonAsync<CreateOrderResponse>())!;
            var stock = await StockAsync(db, laptop.Id);
            var orders = await db.Orders.CountAsync(x => x.CustomerId == _customer.Id);
            var items = await db.OrderItems.CountAsync(x => x.Order.CustomerId == _customer.Id);
            Assert.Equal(5 - quantity, stock);
            Assert.InRange(stock, 1, 2);
            Assert.Equal(1, orders);
            Assert.Equal(1, items);
            Assert.Equal(priorOrders + 1, await db.Orders.CountAsync());
            Assert.Equal(priorItems + 1, await db.OrderItems.CountAsync());
            Assert.Equal(quantity, Assert.Single(result.Items).Quantity);
            Assert.Equal(laptop.Price * quantity, result.TotalAmount);
            _output.WriteLine($"LAPTOP-001 concurrency: A={(int)a.StatusCode}, B={(int)b.StatusCode}, finalStock={stock}, persistedOrders={orders}, persistedItems={items}, independentSqlConnections={gate.ConnectionIds.Count}.");
        }
        finally
        {
            _factory.InventoryGate = null;
        }
    }

    [Fact]
    public async Task Create_ConcurrentSuccessfulOrdersHaveUniqueNumbersAndCorrectStock()
    {
        var gate = new OrdersApiFactory.StockUpdateGate();
        _factory.InventoryGate = gate;
        try
        {
            var responses = await Task.WhenAll(CreateAsync((_first.Id, 1)), CreateAsync((_first.Id, 1)));
            using var a = responses[0];
            using var b = responses[1];
            Assert.All(responses, x => Assert.Equal(HttpStatusCode.Created, x.StatusCode));
            Assert.Equal(2, gate.Arrivals);
            Assert.Equal(2, gate.ConnectionIds.Count);
            var first = (await a.Content.ReadFromJsonAsync<CreateOrderResponse>())!;
            var second = (await b.Content.ReadFromJsonAsync<CreateOrderResponse>())!;
            Assert.NotEqual(first.OrderNumber, second.OrderNumber);
            await using var scope = _factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(8, await StockAsync(db, _first.Id));
            Assert.Equal(2, await db.Orders.CountAsync(x => x.CustomerId == _customer.Id));
        }
        finally
        {
            _factory.InventoryGate = null;
        }
    }

    private Task<HttpResponseMessage> CreateAsync(params (int ProductId, int Quantity)[] items) =>
        _client.PostAsJsonAsync("/api/orders", new
        {
            customerId = _customer.Id,
            items = items.Select(x => new { productId = x.ProductId, quantity = x.Quantity }).ToArray()
        });

    private async Task AuthenticateAsync(string username = "sales", string password = "Sales123!")
    {
        using var response = await _client.PostAsJsonAsync("/api/auth/login", new { username, password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        _actorId = json.RootElement.GetProperty("user").GetProperty("id").GetInt32();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            json.RootElement.GetProperty("token").GetString());
    }

    private async Task AssertNoOrderAsync(int firstStock = 10, int secondStock = 10)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await db.Orders.CountAsync(x => x.CustomerId == _customer.Id));
        Assert.Equal(0, await db.OrderItems.CountAsync(x => x.Order.CustomerId == _customer.Id));
        Assert.Equal(firstStock, await StockAsync(db, _first.Id));
        Assert.Equal(secondStock, await StockAsync(db, _second.Id));
        Assert.Equal(10, await StockAsync(db, _inactive.Id));
    }

    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal((int)status, json.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(code, json.RootElement.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("title").GetString()));
    }

    private string Token(string? subject, string? role)
    {
        var options = _factory.Services.GetRequiredService<IOptions<JwtOptions>>().Value;
        var claims = new List<Claim>();
        if (subject is not null) claims.Add(new Claim(JwtRegisteredClaimNames.Sub, subject));
        if (role is not null) claims.Add(new Claim("role", role));
        var jwt = new JwtSecurityToken(options.Issuer, options.Audience, claims, DateTime.UtcNow.AddMinutes(-1),
            DateTime.UtcNow.AddMinutes(5), new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.Key)), SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }

    private static Task<int> StockAsync(AppDbContext db, int id) =>
        db.Products.Where(x => x.Id == id).Select(x => x.StockQuantity).SingleAsync();

    private static Product Product(string code, decimal price, bool active) => new()
    {
        Code = code, Name = "Order test product", Price = price, StockQuantity = 10, IsActive = active,
        CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
    };
}
