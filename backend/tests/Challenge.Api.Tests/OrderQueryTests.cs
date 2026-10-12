using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Challenge.Api.Auth;
using Challenge.Api.Controllers;
using Challenge.Api.Data;
using Challenge.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OrderDetailsResponse = Challenge.Api.Controllers.OrdersController.OrderDetailsResponse;
using OrderListResponse = Challenge.Api.Controllers.OrdersController.OrderListResponse;

namespace Challenge.Api.Tests;

[Collection("Order database mutations")]
public class OrderQueryTests : IClassFixture<AuthApiFactory>, IAsyncLifetime
{
    private readonly AuthApiFactory _factory;
    private readonly HttpClient _client;
    private readonly string _prefix = "Q5-" + Guid.NewGuid().ToString("N")[..20];
    private readonly Dictionary<string, Order> _orders = new();
    private Customer _customerA = null!;
    private Customer _customerB = null!;
    private Product _productA = null!;
    private Product _productB = null!;
    private int _salesId;

    public OrderQueryTests(AuthApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    public async Task InitializeAsync()
    {
        await AuthenticateAsync();
        await _factory.BeginTestTransactionAsync();
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        _salesId = await db.Users.Where(x => x.Username == "sales").Select(x => x.Id).SingleAsync();
        var adminId = await db.Users.Where(x => x.Username == "admin").Select(x => x.Id).SingleAsync();
        _customerA = new Customer { Code = _prefix + "-A", Name = "Query customer A", IsActive = true };
        _customerB = new Customer { Code = _prefix + "-B", Name = "Query customer B", IsActive = false };
        _productA = new Product { Code = _prefix + "-A", Name = "Query product A", Price = 99.99m,
            StockQuantity = 8, IsActive = true };
        _productB = new Product { Code = _prefix + "-B", Name = "Query product B", Price = 200m,
            StockQuantity = 0, IsActive = false };
        db.Customers.AddRange(_customerA, _customerB);
        db.Products.AddRange(_productA, _productB);
        await db.SaveChangesAsync();

        var day = new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc);
        // Persist in a different order from every supported primary sort. C/D intentionally tie.
        await AddOrderAsync("D", _customerA, day.AddDays(1).AddHours(12), OrderStatus.Confirmed, 30m, _salesId);
        await AddOrderAsync("B", _customerA, day.AddDays(1).AddTicks(-1), OrderStatus.Cancelled, 20m, adminId);
        await AddOrderAsync("E", _customerB, day.AddDays(2), OrderStatus.Cancelled, 50m, _salesId);
        await AddOrderAsync("A", _customerA, day, OrderStatus.Confirmed, 31.73m, _salesId);
        await AddOrderAsync("C", _customerB, day.AddDays(1).AddHours(12), OrderStatus.Confirmed, 30m, _salesId);

        async Task AddOrderAsync(string suffix, Customer customer, DateTime date, OrderStatus status,
            decimal total, int creator)
        {
            var order = new Order { OrderNumber = _prefix + "-" + suffix, CustomerId = customer.Id,
                OrderDate = date, Status = status, TotalAmount = total, CreatedBy = creator,
                CreatedAt = date, UpdatedAt = date };
            if (suffix == "A")
            {
                order.Items.Add(new OrderItem { ProductId = _productB.Id, Quantity = 3,
                    UnitPrice = 2.35m, TotalPrice = 7.05m });
                order.Items.Add(new OrderItem { ProductId = _productA.Id, Quantity = 2,
                    UnitPrice = 12.34m, TotalPrice = 24.68m });
            }
            else
            {
                order.Items.Add(new OrderItem { ProductId = _productA.Id, Quantity = 1,
                    UnitPrice = total, TotalPrice = total });
            }

            db.Orders.Add(order);
            await db.SaveChangesAsync();
            _orders.Add(suffix, order);
        }
    }

    [Fact]
    public async Task List_ReturnsOnlySummaryFieldsAndDefaultPaginationInUtc()
    {
        using var response = await _client.GetAsync(ListPath());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        AssertFields(root, "items", "totalCount", "page", "pageSize");
        Assert.Equal(5, root.GetProperty("totalCount").GetInt32());
        Assert.Equal(1, root.GetProperty("page").GetInt32());
        Assert.Equal(10, root.GetProperty("pageSize").GetInt32());
        var items = root.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(ExpectedIds("ECDBA"), items.Select(x => x.GetProperty("id").GetInt32()));
        Assert.All(items, x => AssertFields(x, "id", "orderNumber", "customerId", "customerName",
            "orderDate", "status", "totalAmount"));
        var a = Assert.Single(items, x => x.GetProperty("id").GetInt32() == _orders["A"].Id);
        Assert.Equal(_orders["A"].OrderNumber, a.GetProperty("orderNumber").GetString());
        Assert.Equal(_customerA.Id, a.GetProperty("customerId").GetInt32());
        Assert.Equal(_customerA.Name, a.GetProperty("customerName").GetString());
        Assert.Equal("Confirmed", a.GetProperty("status").GetString());
        Assert.Equal(31.73m, a.GetProperty("totalAmount").GetDecimal());
        Assert.Equal(_orders["A"].OrderDate, a.GetProperty("orderDate").GetDateTime());
        Assert.Equal(DateTimeKind.Utc, a.GetProperty("orderDate").GetDateTime().Kind);
    }

    [Fact]
    public async Task List_SearchTrimsWhitespaceAndMatchesPartialOrderNumbers()
    {
        var one = await GetListAsync("/api/orders?orderNumber=" + Uri.EscapeDataString("  " + _prefix + "-A  "));
        Assert.Equal(1, one.TotalCount);
        Assert.Equal(_orders["A"].Id, Assert.Single(one.Items).Id);
        var partial = await GetListAsync("/api/orders?orderNumber=" + _prefix[3..]);
        Assert.Equal(5, partial.TotalCount);
        var all = await GetListAsync("/api/orders");
        var whitespace = await GetListAsync("/api/orders?orderNumber=%20%20");
        Assert.Equal(all.TotalCount, whitespace.TotalCount);
        Assert.Equal(all.Items.Select(x => x.Id), whitespace.Items.Select(x => x.Id));
    }

    [Theory]
    [InlineData("missing-number-")]
    [InlineData("%' OR 1=1 --")]
    public async Task List_UnmatchedSearchReturnsEmptyMetadata(string search)
    {
        var result = await GetListAsync("/api/orders?orderNumber=" + Uri.EscapeDataString(search + _prefix));
        Assert.Empty(result.Items);
        Assert.Equal(0, result.TotalCount);
        Assert.Equal(1, result.Page);
        Assert.Equal(10, result.PageSize);
    }

    [Theory]
    [InlineData(true, "DBA")]
    [InlineData(false, "EC")]
    public async Task List_FiltersCustomerIncludingHistoricalInactiveCustomer(bool active, string expected)
    {
        var customer = active ? _customerA : _customerB;
        var result = await GetListAsync(ListPath("&customerId=" + customer.Id));
        Assert.Equal(expected.Length, result.TotalCount);
        Assert.Equal(ExpectedIds(expected), result.Items.Select(x => x.Id));
        Assert.All(result.Items, x => Assert.Equal(customer.Id, x.CustomerId));
    }

    [Fact]
    public async Task List_UnknownCustomerReturnsEmptyResults()
    {
        var result = await GetListAsync(ListPath("&customerId=2147483647"));
        Assert.Empty(result.Items);
        Assert.Equal(0, result.TotalCount);
    }

    [Theory]
    [InlineData("Confirmed", "CDA")]
    [InlineData("Cancelled", "EB")]
    [InlineData("1", "CDA")]
    [InlineData("2", "EB")]
    public async Task List_FiltersExistingStatuses(string status, string expected)
    {
        var result = await GetListAsync(ListPath("&status=" + status));
        Assert.Equal(expected.Length, result.TotalCount);
        Assert.Equal(ExpectedIds(expected), result.Items.Select(x => x.Id));
    }

    [Theory]
    [InlineData("&fromDate=2026-01-10&toDate=2026-01-10", "BA")]
    [InlineData("&fromDate=2026-01-10&toDate=2026-01-11", "CDBA")]
    [InlineData("&fromDate=2026-01-11", "ECD")]
    [InlineData("&toDate=2026-01-10", "BA")]
    [InlineData("&toDate=2026-01-09", "")]
    [InlineData("&fromDate=2026-01-12&toDate=2026-01-12", "E")]
    [InlineData("&fromDate=0001-01-01&toDate=9999-12-31", "ECDBA")]
    public async Task List_DateRangeIncludesEntireUtcDays(string query, string expected)
    {
        var result = await GetListAsync(ListPath(query));
        Assert.Equal(expected.Length, result.TotalCount);
        Assert.Equal(ExpectedIds(expected), result.Items.Select(x => x.Id));
    }

    [Theory]
    [InlineData("orderDate", "asc", "ABDCE")]
    [InlineData("orderDate", "desc", "ECDBA")]
    [InlineData("orderNumber", "asc", "ABCDE")]
    [InlineData("orderNumber", "desc", "EDCBA")]
    [InlineData("totalAmount", "asc", "BDCAE")]
    [InlineData("totalAmount", "desc", "EACDB")]
    [InlineData(" ORDERDATE ", " DESC ", "ECDBA")]
    public async Task List_SortsInBothDirectionsWithIdTieBreaker(string sort, string direction, string expected)
    {
        var result = await GetListAsync(ListPath("&sortBy=" + Uri.EscapeDataString(sort) +
            "&sortDirection=" + Uri.EscapeDataString(direction)));
        Assert.Equal(ExpectedIds(expected), result.Items.Select(x => x.Id));
    }

    [Fact]
    public async Task List_PaginationRemainsStableAcrossTiesAndPreservesTotalCount()
    {
        var first = await GetListAsync(ListPath("&page=1&pageSize=2"));
        var second = await GetListAsync(ListPath("&page=2&pageSize=2"));
        var third = await GetListAsync(ListPath("&page=3&pageSize=2"));
        var beyond = await GetListAsync(ListPath("&page=4&pageSize=2"));
        var repeated = await GetListAsync(ListPath("&page=1&pageSize=2"));
        Assert.Equal(ExpectedIds("EC"), first.Items.Select(x => x.Id));
        Assert.Equal(ExpectedIds("DB"), second.Items.Select(x => x.Id));
        Assert.Equal(_orders["A"].Id, Assert.Single(third.Items).Id);
        Assert.Empty(beyond.Items);
        Assert.Equal(first.Items.Select(x => x.Id), repeated.Items.Select(x => x.Id));
        Assert.Equal(5, first.Items.Concat(second.Items).Concat(third.Items).Select(x => x.Id).Distinct().Count());
        Assert.All(new[] { first, second, third, beyond }, x => { Assert.Equal(5, x.TotalCount);
            Assert.Equal(2, x.PageSize); });
        Assert.Equal(2, second.Page);
        Assert.Equal(4, beyond.Page);
    }

    [Fact]
    public async Task List_AppliesCombinedFiltersCountSortingAndPaginationInSql()
    {
        _factory.ExecutedQueries.Clear();
        var result = await GetListAsync(ListPath("&customerId=" + _customerA.Id +
            "&status=Confirmed&fromDate=2026-01-11&toDate=2026-01-11&pageSize=1"));
        Assert.Equal(1, result.TotalCount);
        Assert.Equal(_orders["D"].Id, Assert.Single(result.Items).Id);
        var queries = _factory.ExecutedQueries.Where(x => x.Contains("FROM [Orders]")).ToArray();
        Assert.Equal(2, queries.Length);
        var count = Assert.Single(queries, x => x.Contains("COUNT(*)"));
        var page = Assert.Single(queries, x => x.Contains("OFFSET"));
        foreach (var query in new[] { count, page })
        {
            Assert.Contains("WHERE", query);
            Assert.Contains("[OrderNumber]", query);
            Assert.True(query.Contains("LIKE") || query.Contains("CHARINDEX"));
            Assert.Contains("[CustomerId]", query);
            Assert.Contains("[Status]", query);
            Assert.Contains("[OrderDate] >=", query);
            Assert.Contains("[OrderDate] <", query);
        }

        Assert.Contains("FETCH NEXT", page);
        Assert.Matches(@"ORDER BY \[[^\]]+\]\.\[OrderDate\] DESC, \[[^\]]+\]\.\[Id\] DESC", page);
        Assert.DoesNotContain("[OrderItems]", page);
        Assert.DoesNotContain("[PasswordHash]", page);
        Assert.DoesNotContain("[StockQuantity]", page);
    }

    [Theory]
    [InlineData("page=0")]
    [InlineData("page=-1")]
    [InlineData("page=-2147483648")]
    [InlineData("pageSize=0")]
    [InlineData("pageSize=-1")]
    [InlineData("pageSize=101")]
    [InlineData("page=2147483647&pageSize=2")]
    [InlineData("page=abc")]
    [InlineData("pageSize=abc")]
    [InlineData("page=2147483648")]
    [InlineData("customerId=0")]
    [InlineData("customerId=-1")]
    [InlineData("customerId=abc")]
    [InlineData("status=0")]
    [InlineData("status=3")]
    [InlineData("status=unknown")]
    [InlineData("sortBy=unknown")]
    [InlineData("sortDirection=sideways")]
    [InlineData("fromDate=2026-01-12&toDate=2026-01-10")]
    [InlineData("fromDate=not-a-date")]
    [InlineData("toDate=2026-02-30")]
    public async Task List_InvalidParametersReturnValidationProblem(string query)
    {
        _factory.ExecutedQueries.Clear();
        using var response = await _client.GetAsync(ListPath("&" + query));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(400, json.RootElement.GetProperty("status").GetInt32());
        Assert.NotEmpty(json.RootElement.GetProperty("errors").EnumerateObject());
        Assert.DoesNotContain(_factory.ExecutedQueries, x => x.Contains("FROM [Orders]"));
    }

    [Theory]
    [InlineData("&pageSize=100", 5)]
    [InlineData("&page=2147483647&pageSize=1", 0)]
    public async Task List_ValidPaginationBoundariesDoNotOverflow(string query, int size)
    {
        var result = await GetListAsync(ListPath(query));
        Assert.Equal(5, result.TotalCount);
        Assert.Equal(size, result.Items.Count);
    }

    [Fact]
    public async Task Details_ReturnsCustomerCreatorAndMultipleHistoricalItemsInOneSmallSqlQuery()
    {
        _factory.ExecutedQueries.Clear();
        using var response = await _client.GetAsync("/api/orders/" + _orders["A"].Id);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        AssertFields(root, "id", "orderNumber", "customerId", "orderDate", "status", "totalAmount",
            "createdBy", "customer", "createdByUser", "items");
        Assert.Equal(_orders["A"].Id, root.GetProperty("id").GetInt32());
        Assert.Equal(_orders["A"].OrderNumber, root.GetProperty("orderNumber").GetString());
        Assert.Equal(_customerA.Id, root.GetProperty("customerId").GetInt32());
        Assert.Equal(_orders["A"].OrderDate, root.GetProperty("orderDate").GetDateTime());
        Assert.Equal(DateTimeKind.Utc, root.GetProperty("orderDate").GetDateTime().Kind);
        Assert.Equal("Confirmed", root.GetProperty("status").GetString());
        Assert.Equal(31.73m, root.GetProperty("totalAmount").GetDecimal());
        Assert.Equal(_salesId, root.GetProperty("createdBy").GetInt32());
        var customer = root.GetProperty("customer");
        AssertFields(customer, "id", "code", "name");
        Assert.Equal(_customerA.Id, customer.GetProperty("id").GetInt32());
        Assert.Equal(_customerA.Code, customer.GetProperty("code").GetString());
        Assert.Equal(_customerA.Name, customer.GetProperty("name").GetString());
        var creator = root.GetProperty("createdByUser");
        AssertFields(creator, "id", "username");
        Assert.Equal(_salesId, creator.GetProperty("id").GetInt32());
        Assert.Equal("sales", creator.GetProperty("username").GetString());
        var items = root.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(2, items.Length);
        Assert.Equal(new[] { _productA.Id, _productB.Id }.OrderBy(x => x),
            items.Select(x => x.GetProperty("productId").GetInt32()));
        Assert.All(items, x => AssertFields(x, "productId", "productCode", "productName", "quantity",
            "unitPrice", "totalPrice"));
        var productA = Assert.Single(items, x => x.GetProperty("productId").GetInt32() == _productA.Id);
        Assert.Equal(_productA.Code, productA.GetProperty("productCode").GetString());
        Assert.Equal(_productA.Name, productA.GetProperty("productName").GetString());
        Assert.Equal(2, productA.GetProperty("quantity").GetInt32());
        Assert.Equal(12.34m, productA.GetProperty("unitPrice").GetDecimal());
        Assert.Equal(24.68m, productA.GetProperty("totalPrice").GetDecimal());
        var productB = Assert.Single(items, x => x.GetProperty("productId").GetInt32() == _productB.Id);
        Assert.Equal(2.35m, productB.GetProperty("unitPrice").GetDecimal());
        Assert.Equal(7.05m, productB.GetProperty("totalPrice").GetDecimal());
        var sql = Assert.Single(_factory.ExecutedQueries, x => x.Contains("FROM [Orders]"));
        Assert.Contains("[OrderItems]", sql);
        Assert.Contains("[Products]", sql);
        Assert.Contains("[Customers]", sql);
        Assert.Contains("[Users]", sql);
        Assert.Contains("[UnitPrice]", sql);
        Assert.Contains("[TotalPrice]", sql);
        Assert.DoesNotContain("[Price]", sql);
        Assert.DoesNotContain("[PasswordHash]", sql);
        Assert.DoesNotContain("[StockQuantity]", sql);
        Assert.DoesNotContain("[PhoneNumber]", sql);
    }

    [Fact]
    public async Task Details_ProductPriceChangesDoNotChangeSavedHistoricalAmounts()
    {
        var before = await GetDetailsAsync(_orders["A"].Id);
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Products.Where(x => x.Id == _productA.Id || x.Id == _productB.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Price, 555.55m));
            Assert.Equal(555.55m, await db.Products.Where(x => x.Id == _productA.Id)
                .Select(x => x.Price).SingleAsync());
        }

        var after = await GetDetailsAsync(_orders["A"].Id);
        Assert.Equal(before.TotalAmount, after.TotalAmount);
        Assert.Equal(before.Items.Select(x => (x.ProductId, x.Quantity, x.UnitPrice, x.TotalPrice)),
            after.Items.Select(x => (x.ProductId, x.Quantity, x.UnitPrice, x.TotalPrice)));
        Assert.Equal(12.34m, after.Items.Single(x => x.ProductId == _productA.Id).UnitPrice);
        var list = await GetListAsync(ListPath());
        Assert.Equal(31.73m, list.Items.Single(x => x.Id == after.Id).TotalAmount);
    }

    [Fact]
    public async Task Details_ReturnsStoredTotalsWithoutRecalculatingThem()
    {
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.OrderItems.Where(x => x.OrderId == _orders["A"].Id && x.ProductId == _productA.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.TotalPrice, 21.23m));
            await db.Orders.Where(x => x.Id == _orders["A"].Id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.TotalAmount, 28.28m));
        }

        var result = await GetDetailsAsync(_orders["A"].Id);
        Assert.Equal(28.28m, result.TotalAmount);
        Assert.Equal(21.23m, result.Items.Single(x => x.ProductId == _productA.Id).TotalPrice);
        Assert.Equal(12.34m, result.Items.Single(x => x.ProductId == _productA.Id).UnitPrice);
    }

    [Fact]
    public async Task Details_CancelledOrderAndInactiveCustomerRemainReadable()
    {
        var result = await GetDetailsAsync(_orders["E"].Id);
        Assert.Equal("Cancelled", result.Status);
        Assert.Equal(_customerB.Id, result.Customer.Id);
        Assert.Equal(_customerB.Name, result.Customer.Name);
    }

    [Theory]
    [InlineData("0", HttpStatusCode.BadRequest, "invalid_order_id")]
    [InlineData("-1", HttpStatusCode.BadRequest, "invalid_order_id")]
    [InlineData("2147483647", HttpStatusCode.NotFound, "order_not_found")]
    public async Task Details_InvalidOrMissingIdReturnsProblem(string id, HttpStatusCode status, string code)
    {
        using var response = await _client.GetAsync("/api/orders/" + id);
        Assert.Equal(status, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, json.RootElement.GetProperty("code").GetString());
        Assert.Equal((int)status, json.RootElement.GetProperty("status").GetInt32());
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("2147483648")]
    public async Task Details_NonIntegerRouteReturnsNotFound(string id)
    {
        using var response = await _client.GetAsync("/api/orders/" + id);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Queries_WithoutTokenReturnUnauthorized(bool details)
    {
        _client.DefaultRequestHeaders.Authorization = null;
        using var response = await _client.GetAsync(details ? "/api/orders/" + _orders["A"].Id : ListPath());
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(false, "Unknown")]
    [InlineData(true, "Unknown")]
    public async Task Queries_WithoutAllowedRoleReturnForbidden(bool details, string? role)
    {
        var options = _factory.Services.GetRequiredService<IOptions<JwtOptions>>().Value;
        var claims = new List<Claim> { new(JwtRegisteredClaimNames.Sub, _salesId.ToString()),
            new(JwtRegisteredClaimNames.UniqueName, "sales") };
        if (role is not null) claims.Add(new Claim("role", role));
        var jwt = new JwtSecurityToken(options.Issuer, options.Audience, claims, DateTime.UtcNow.AddMinutes(-1),
            DateTime.UtcNow.AddMinutes(5), new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.Key)), SecurityAlgorithms.HmacSha256));
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            new JwtSecurityTokenHandler().WriteToken(jwt));
        using var response = await _client.GetAsync(details ? "/api/orders/" + _orders["A"].Id : ListPath());
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("admin", "Admin123!")]
    [InlineData("sales", "Sales123!")]
    public async Task Queries_BothExistingRolesCanReadOrders(string username, string password)
    {
        await AuthenticateAsync(username, password);
        Assert.Equal(5, (await GetListAsync(ListPath())).TotalCount);
        Assert.Equal(_orders["A"].Id, (await GetDetailsAsync(_orders["A"].Id)).Id);
    }

    private string ListPath(string query = "") => "/api/orders?orderNumber=" + _prefix + query;
    private IEnumerable<int> ExpectedIds(string suffixes) => suffixes.Select(x => _orders[x.ToString()].Id);
    private static void AssertFields(JsonElement value, params string[] names) =>
        Assert.Equal(names.OrderBy(x => x), value.EnumerateObject().Select(x => x.Name).OrderBy(x => x));

    private async Task<OrderListResponse> GetListAsync(string path)
    {
        using var response = await _client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<OrderListResponse>())!;
    }

    private async Task<OrderDetailsResponse> GetDetailsAsync(int id)
    {
        using var response = await _client.GetAsync("/api/orders/" + id);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<OrderDetailsResponse>())!;
    }

    private async Task AuthenticateAsync(string username = "sales", string password = "Sales123!")
    {
        using var response = await _client.PostAsJsonAsync("/api/auth/login", new { username, password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            json.RootElement.GetProperty("token").GetString());
    }

    public async Task DisposeAsync()
    {
        try
        {
            await _factory.RollbackTestTransactionAsync();
            await using var scope = _factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.False(await db.Orders.AnyAsync(x => x.OrderNumber.StartsWith(_prefix)));
            Assert.False(await db.OrderItems.AnyAsync(x => x.Order.OrderNumber.StartsWith(_prefix)));
            Assert.False(await db.Products.AnyAsync(x => x.Code.StartsWith(_prefix)));
            Assert.False(await db.Customers.AnyAsync(x => x.Code.StartsWith(_prefix)));
        }
        finally
        {
            _client.Dispose();
        }
    }
}
