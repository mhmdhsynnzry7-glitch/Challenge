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
using TopProductResponse = Challenge.Api.Controllers.ReportsController.TopProductResponse;

namespace Challenge.Api.Tests;

[Collection("Order database mutations")]
public class ReportTests : IClassFixture<AuthApiFactory>, IAsyncLifetime
{
    private static readonly DateTime SalesDay = new(2001, 6, 10, 0, 0, 0, DateTimeKind.Utc);
    private const string SalesRange = "?fromDate=2001-06-10&toDate=2001-06-11";
    private readonly AuthApiFactory _factory;
    private readonly HttpClient _client;
    private readonly ITestOutputHelper _output;
    private readonly string _prefix = "R6-" + Guid.NewGuid().ToString("N")[..20];
    private readonly Dictionary<string, Product> _products = new();
    private Customer _customer = null!;
    private Order _firstOrder = null!;
    private int _salesId;

    public ReportTests(AuthApiFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _output = output;
    }

    public async Task InitializeAsync()
    {
        await AuthenticateAsync();
        await _factory.BeginTestTransactionAsync();
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        _salesId = await db.Users.Where(x => x.Username == "sales").Select(x => x.Id).SingleAsync();
        _customer = new Customer { Code = _prefix, Name = "Historical report customer", IsActive = false };
        db.Customers.Add(_customer);
        // B precedes A by ID, while codes must break their equal-quantity tie A before B.
        foreach (var suffix in new[] { "B", "C", "A", "N" })
        {
            var product = new Product { Code = _prefix + "-" + suffix,
                Name = suffix is "A" or "B" ? "Same display name" : "Report product " + suffix,
                Price = 1000m, StockQuantity = 0, IsActive = suffix != "C" };
            db.Products.Add(product);
            await db.SaveChangesAsync();
            _products.Add(suffix, product);
        }

        await AddOrderAsync("before", SalesDay.AddTicks(-1), OrderStatus.Confirmed, ("A", 40, 1m));
        _firstOrder = await AddOrderAsync("start", SalesDay, OrderStatus.Confirmed,
            ("A", 2, 12.34m), ("B", 1, 100m));
        await AddOrderAsync("end", SalesDay.AddDays(1).AddTicks(-1), OrderStatus.Confirmed,
            ("A", 3, 15m), ("C", 2, 250m));
        await AddOrderAsync("next", SalesDay.AddDays(1).AddHours(12), OrderStatus.Confirmed, ("B", 4, 2.50m));
        await AddOrderAsync("after", SalesDay.AddDays(2), OrderStatus.Confirmed, ("A", 20, 3m));
        await AddOrderAsync("cancel", SalesDay.AddHours(8), OrderStatus.Cancelled,
            ("A", 100, 10m), ("N", 1000, 1m));

        async Task<Order> AddOrderAsync(string suffix, DateTime date, OrderStatus status,
            params (string Product, int Quantity, decimal Price)[] items)
        {
            var order = new Order { OrderNumber = _prefix + "-" + suffix, CustomerId = _customer.Id,
                OrderDate = date, Status = status, CreatedBy = _salesId, CreatedAt = date, UpdatedAt = date };
            foreach (var item in items)
            {
                var total = item.Quantity * item.Price;
                order.Items.Add(new OrderItem { ProductId = _products[item.Product].Id,
                    Quantity = item.Quantity, UnitPrice = item.Price, TotalPrice = total });
                order.TotalAmount += total;
            }

            db.Orders.Add(order);
            await db.SaveChangesAsync();
            return order;
        }
    }

    [Fact]
    public async Task Report_ReturnsExactArrayContractAndAggregatesProductsAcrossOrders()
    {
        using var response = await _client.GetAsync("/api/reports/top-products" + SalesRange);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Array, json.RootElement.ValueKind);
        var rows = json.RootElement.EnumerateArray().ToArray();
        Assert.Equal(3, rows.Length);
        Assert.All(rows, row => Assert.Equal(new[] { "productCode", "productName", "quantitySold", "totalSales" },
            row.EnumerateObject().Select(x => x.Name).OrderBy(x => x)));
        Assert.Equal(new[] { Code("A"), Code("B"), Code("C") },
            rows.Select(x => x.GetProperty("productCode").GetString()));
        var a = rows[0];
        Assert.Equal(_products["A"].Name, a.GetProperty("productName").GetString());
        Assert.Equal(5L, a.GetProperty("quantitySold").GetInt64());
        Assert.Equal(69.68m, a.GetProperty("totalSales").GetDecimal());
        Assert.Equal(5L, rows[1].GetProperty("quantitySold").GetInt64());
        Assert.Equal(110m, rows[1].GetProperty("totalSales").GetDecimal());
        Assert.Equal(2L, rows[2].GetProperty("quantitySold").GetInt64());
        Assert.Equal(500m, rows[2].GetProperty("totalSales").GetDecimal());
    }

    [Theory]
    [InlineData("?fromDate=2001-06-10&toDate=2001-06-10", "ACB", 5L, 1L, 2L)]
    [InlineData("?fromDate=2001-06-11&toDate=2001-06-11", "B", 0L, 4L, 0L)]
    [InlineData("?fromDate=2001-06-12&toDate=2001-06-12", "A", 20L, 0L, 0L)]
    [InlineData("?fromDate=2001-06-09&toDate=2001-06-09", "A", 40L, 0L, 0L)]
    [InlineData("?fromDate=2001-06-10", "ABC", 25L, 5L, 2L)]
    [InlineData("?toDate=2001-06-11", "ABC", 45L, 5L, 2L)]
    [InlineData("", "ABC", 65L, 5L, 2L)]
    [InlineData("?fromDate=0001-01-01&toDate=9999-12-31", "ABC", 65L, 5L, 2L)]
    public async Task Report_DateFiltersIncludeEntireUtcDaysAndAllowOmittedBoundaries(
        string query, string expectedOrder, long a, long b, long c)
    {
        var rows = FixtureRows(await GetReportAsync(query));
        Assert.Equal(expectedOrder.Select(x => Code(x.ToString())), rows.Select(x => x.ProductCode));
        Assert.Equal(a, rows.SingleOrDefault(x => x.ProductCode == Code("A"))?.QuantitySold ?? 0);
        Assert.Equal(b, rows.SingleOrDefault(x => x.ProductCode == Code("B"))?.QuantitySold ?? 0);
        Assert.Equal(c, rows.SingleOrDefault(x => x.ProductCode == Code("C"))?.QuantitySold ?? 0);
    }

    [Theory]
    [InlineData("?fromDate=2001-06-08&toDate=2001-06-08")]
    [InlineData("?fromDate=2001-06-13&toDate=2001-06-14")]
    public async Task Report_NoMatchingSalesReturnsEmptyArray(string query)
    {
        using var response = await _client.GetAsync("/api/reports/top-products" + query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("[]", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Report_ExcludesCancelledSalesAndIncludesHistoricalInactiveProductsAndCustomers()
    {
        var rows = await GetReportAsync(SalesRange);
        Assert.DoesNotContain(rows, x => x.ProductCode == Code("N"));
        Assert.Equal(5L, rows.Single(x => x.ProductCode == Code("A")).QuantitySold);
        Assert.Equal(2L, rows.Single(x => x.ProductCode == Code("C")).QuantitySold);
        Assert.False(_products["C"].IsActive);
        Assert.False(_customer.IsActive);
    }

    [Fact]
    public async Task Report_OnlyCancelledOrdersReturnsEmptyArray()
    {
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Orders.Where(x => x.OrderNumber.StartsWith(_prefix))
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, OrderStatus.Cancelled));
        }

        Assert.Empty(await GetReportAsync(SalesRange));
    }

    [Fact]
    public async Task Report_ProductPriceChangesDoNotChangeHistoricalSales()
    {
        var before = await GetReportAsync(SalesRange);
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Products.Where(x => x.Code.StartsWith(_prefix))
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Price, 9876.54m));
            Assert.Equal(9876.54m, await db.Products.Where(x => x.Id == _products["A"].Id)
                .Select(x => x.Price).SingleAsync());
        }

        var after = await GetReportAsync(SalesRange);
        Assert.Equal(before, after);
        Assert.Equal(69.68m, after.Single(x => x.ProductCode == Code("A")).TotalSales);
    }

    [Fact]
    public async Task Report_SumsStoredItemTotalsWithoutRecalculatingFromUnitPricesOrOrderTotals()
    {
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.OrderItems.Where(x => x.OrderId == _firstOrder.Id && x.ProductId == _products["A"].Id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.TotalPrice, 21.23m));
            await db.Orders.Where(x => x.Id == _firstOrder.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.TotalAmount, 121.23m));
        }

        var rows = await GetReportAsync(SalesRange);
        Assert.Equal(66.23m, rows.Single(x => x.ProductCode == Code("A")).TotalSales);
        Assert.Equal(110m, rows.Single(x => x.ProductCode == Code("B")).TotalSales);
    }

    [Fact]
    public async Task Report_SortsByQuantityAndBreaksTiesByCodeRatherThanAmountOrInsertionOrder()
    {
        Assert.True(_products["B"].Id < _products["A"].Id);
        var first = await GetReportAsync(SalesRange);
        var repeated = await GetReportAsync(SalesRange);
        Assert.Equal(new[] { Code("A"), Code("B"), Code("C") }, first.Select(x => x.ProductCode));
        Assert.Equal(5L, first[0].QuantitySold);
        Assert.Equal(5L, first[1].QuantitySold);
        Assert.True(first[2].TotalSales > first[0].TotalSales);
        Assert.Equal(first, repeated);
    }

    [Fact]
    public async Task Report_QuantitySumUsesBigIntAndCanExceedSingleItemIntegerRange()
    {
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            for (var i = 0; i < 2; i++)
            {
                db.Orders.Add(new Order { OrderNumber = _prefix + "-big" + i,
                    CustomerId = _customer.Id, CreatedBy = _salesId, OrderDate = SalesDay,
                    Status = OrderStatus.Confirmed, CreatedAt = SalesDay, UpdatedAt = SalesDay,
                    Items = new List<OrderItem> { new() { ProductId = _products["N"].Id,
                        Quantity = int.MaxValue, UnitPrice = 0m, TotalPrice = 0m } } });
            }

            await db.SaveChangesAsync();
        }

        var result = await GetReportAsync(SalesRange);
        Assert.Equal(Code("N"), result[0].ProductCode);
        Assert.Equal(2L * int.MaxValue, result[0].QuantitySold);
        Assert.Equal(0m, result[0].TotalSales);
    }

    [Fact]
    public async Task Report_FiltersGroupsAggregatesAndSortsInOneSqlQuery()
    {
        _factory.ExecutedQueries.Clear();
        Assert.Equal(3, (await GetReportAsync(SalesRange)).Count);
        var sql = Assert.Single(_factory.ExecutedQueries);
        _output.WriteLine(sql);
        Assert.Contains("FROM [OrderItems]", sql);
        Assert.Contains("[Orders]", sql);
        Assert.Contains("[Products]", sql);
        Assert.Contains("WHERE", sql);
        Assert.Contains("[Status] = 1", sql);
        Assert.Contains("[OrderDate] >=", sql);
        Assert.Contains("[OrderDate] <", sql);
        Assert.Contains("GROUP BY", sql);
        Assert.Matches(@"SUM\(CAST\(\[[^\]]+\]\.\[Quantity\] AS bigint\)\)", sql);
        Assert.Matches(@"SUM\(\[[^\]]+\]\.\[TotalPrice\]\)", sql);
        Assert.Matches(@"ORDER BY [\s\S]* DESC, \[[^\]]+\]\.\[Code\], \[[^\]]+\]\.\[ProductId\]", sql);
        Assert.DoesNotContain("[Price]", sql);
        Assert.DoesNotContain("[UnitPrice]", sql);
        Assert.DoesNotContain("[TotalAmount]", sql);
        Assert.DoesNotContain("[PasswordHash]", sql);
        Assert.DoesNotContain("[StockQuantity]", sql);
    }

    [Theory]
    [InlineData("?fromDate=2001-06-12&toDate=2001-06-10")]
    [InlineData("?fromDate=not-a-date")]
    [InlineData("?toDate=invalid")]
    [InlineData("?fromDate=2001-02-30")]
    [InlineData("?toDate=2001-13-01")]
    [InlineData("?fromDate=10000-01-01")]
    [InlineData("?toDate=0000-01-01")]
    public async Task Report_InvalidDatesReturnValidationProblemWithoutQueryingSales(string query)
    {
        _factory.ExecutedQueries.Clear();
        using var response = await _client.GetAsync("/api/reports/top-products" + query);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(400, json.RootElement.GetProperty("status").GetInt32());
        Assert.NotEmpty(json.RootElement.GetProperty("errors").EnumerateObject());
        Assert.Empty(_factory.ExecutedQueries);
    }

    [Fact]
    public async Task Report_WithoutTokenReturnsUnauthorized()
    {
        _client.DefaultRequestHeaders.Authorization = null;
        using var response = await _client.GetAsync("/api/reports/top-products" + SalesRange);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Unknown")]
    public async Task Report_WithoutAllowedRoleReturnsForbidden(string? role)
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
        using var response = await _client.GetAsync("/api/reports/top-products" + SalesRange);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("admin", "Admin123!")]
    [InlineData("sales", "Sales123!")]
    public async Task Report_BothExistingRolesCanReadSales(string username, string password)
    {
        await AuthenticateAsync(username, password);
        Assert.Equal(3, (await GetReportAsync(SalesRange)).Count);
    }

    private string Code(string suffix) => _products[suffix].Code;
    private IReadOnlyList<TopProductResponse> FixtureRows(IReadOnlyList<TopProductResponse> rows) =>
        rows.Where(x => x.ProductCode.StartsWith(_prefix, StringComparison.Ordinal)).ToArray();

    private async Task<IReadOnlyList<TopProductResponse>> GetReportAsync(string query)
    {
        using var response = await _client.GetAsync("/api/reports/top-products" + query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<List<TopProductResponse>>())!;
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
