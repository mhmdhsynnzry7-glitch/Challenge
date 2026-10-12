using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Challenge.Api.Data;
using Challenge.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProductListResponse = Challenge.Api.Controllers.ProductsController.ProductListResponse;

namespace Challenge.Api.Tests;

public class CatalogTests : IClassFixture<AuthApiFactory>, IDisposable
{
    private readonly AuthApiFactory _factory;
    private readonly HttpClient _client;

    public CatalogTests(AuthApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Products_ReturnsSmallDtosAndDefaultPagination()
    {
        await AuthenticateAsync();
        using var response = await _client.GetAsync("/api/products");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        Assert.Equal(new[] { "items", "page", "pageSize", "totalCount" },
            root.EnumerateObject().Select(x => x.Name).OrderBy(x => x));
        Assert.Equal(1, root.GetProperty("page").GetInt32());
        Assert.Equal(10, root.GetProperty("pageSize").GetInt32());
        var items = root.GetProperty("items").EnumerateArray().ToArray();
        Assert.NotEmpty(items);
        Assert.InRange(items.Length, 1, 10);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(await db.Products.CountAsync(x => x.IsActive), root.GetProperty("totalCount").GetInt32());
        foreach (var item in items)
        {
            Assert.Equal(new[] { "code", "id", "isActive", "name", "price", "stockQuantity" },
                item.EnumerateObject().Select(x => x.Name).OrderBy(x => x));
            Assert.True(item.GetProperty("isActive").GetBoolean());
        }

        var first = items[0];
        var id = first.GetProperty("id").GetInt32();
        var product = await db.Products.AsNoTracking().SingleAsync(x => x.Id == id);
        Assert.Equal(product.Code, first.GetProperty("code").GetString());
        Assert.Equal(product.Name, first.GetProperty("name").GetString());
        Assert.Equal(product.Price, first.GetProperty("price").GetDecimal());
        Assert.Equal(product.StockQuantity, first.GetProperty("stockQuantity").GetInt32());
    }

    [Theory]
    [InlineData("APTOP-00")]
    [InlineData("14 inch")]
    [InlineData("  LAPTOP-001  ")]
    public async Task Products_SearchMatchesCodeAndName(string search)
    {
        await AuthenticateAsync();
        var result = await GetProductsAsync("?search=" + Uri.EscapeDataString(search));
        Assert.Equal(1, result.TotalCount);
        Assert.Equal("LAPTOP-001", Assert.Single(result.Items).Code);
    }

    [Fact]
    public async Task Products_WhitespaceSearchDoesNotRestrictResults()
    {
        await AuthenticateAsync();
        var normal = await GetProductsAsync("");
        var whitespace = await GetProductsAsync("?search=%20%20%20");
        Assert.Equal(normal.TotalCount, whitespace.TotalCount);
        Assert.Equal(normal.Items.Select(x => x.Id), whitespace.Items.Select(x => x.Id));
    }

    [Theory]
    [InlineData("no-such-product-for-step3")]
    [InlineData("%' OR 1=1 --")]
    public async Task Products_UnmatchedOrSqlLikeSearchReturnsEmptyResults(string search)
    {
        await AuthenticateAsync();
        var result = await GetProductsAsync("?search=" + Uri.EscapeDataString(search));
        Assert.Empty(result.Items);
        Assert.Equal(0, result.TotalCount);
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("&isActive=true", true)]
    [InlineData("&isActive=false", false)]
    public async Task Products_StatusFilterDefaultsToActiveAndSupportsInactive(string filter, bool expectedActive)
    {
        await AuthenticateAsync();
        await _factory.BeginTestTransactionAsync();
        try
        {
            await using var scope = _factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var prefix = "P3-" + Guid.NewGuid().ToString("N")[..8];
            db.Products.AddRange(
                Product(prefix + "-A", true),
                Product(prefix + "-I", false));
            await db.SaveChangesAsync();

            var result = await GetProductsAsync("?search=" + prefix + filter);
            Assert.Equal(1, result.TotalCount);
            var item = Assert.Single(result.Items);
            Assert.Equal(expectedActive, item.IsActive);
            Assert.Equal(prefix + (expectedActive ? "-A" : "-I"), item.Code);
        }
        finally
        {
            await _factory.RollbackTestTransactionAsync();
        }
    }

    [Fact]
    public async Task Products_PaginatesAndFiltersInSqlWithStableOrderingAndCorrectCount()
    {
        await AuthenticateAsync();
        await _factory.BeginTestTransactionAsync();
        try
        {
            await using var scope = _factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var prefix = "P3-" + Guid.NewGuid().ToString("N")[..8];
            db.Products.AddRange(new[] { "C", "A", "E", "B", "D" }
                .Select(suffix => Product(prefix + "-" + suffix, true)));
            db.Products.Add(Product(prefix + "-F", false));
            db.Products.Add(Product("P3-other-" + Guid.NewGuid().ToString("N")[..8], true));
            await db.SaveChangesAsync();

            var first = await GetProductsAsync("?search=" + prefix + "&page=1&pageSize=2");
            _factory.ExecutedQueries.Clear();
            var second = await GetProductsAsync("?search=" + prefix + "&page=2&pageSize=2");
            var queries = _factory.ExecutedQueries.Where(x => x.Contains("FROM [Products]")).ToArray();
            Assert.Equal(2, queries.Length);
            Assert.Contains(queries, x => x.Contains("COUNT(*)"));
            var pageQuery = Assert.Single(queries, x => x.Contains("OFFSET"));
            Assert.Contains("WHERE", pageQuery);
            Assert.Contains("[IsActive]", pageQuery);
            Assert.True(pageQuery.Contains("LIKE") || pageQuery.Contains("CHARINDEX"));
            Assert.Contains("ORDER BY", pageQuery);
            Assert.Contains("FETCH NEXT", pageQuery);

            var third = await GetProductsAsync("?search=" + prefix + "&page=3&pageSize=2");
            var beyond = await GetProductsAsync("?search=" + prefix + "&page=4&pageSize=2");
            var repeated = await GetProductsAsync("?search=" + prefix + "&page=1&pageSize=2");
            Assert.Equal(new[] { prefix + "-A", prefix + "-B" }, first.Items.Select(x => x.Code));
            Assert.Equal(new[] { prefix + "-C", prefix + "-D" }, second.Items.Select(x => x.Code));
            Assert.Equal(prefix + "-E", Assert.Single(third.Items).Code);
            Assert.Empty(beyond.Items);
            Assert.Equal(first.Items.Select(x => x.Id), repeated.Items.Select(x => x.Id));
            Assert.All(new[] { first, second, third, beyond }, x => Assert.Equal(5, x.TotalCount));
            Assert.Equal(2, second.Page);
            Assert.Equal(2, second.PageSize);
            Assert.Equal(5, first.Items.Concat(second.Items).Concat(third.Items).Select(x => x.Id).Distinct().Count());
        }
        finally
        {
            await _factory.RollbackTestTransactionAsync();
        }
    }

    [Theory]
    [InlineData("page=0")]
    [InlineData("page=-1")]
    [InlineData("page=-2147483648")]
    [InlineData("pageSize=0")]
    [InlineData("pageSize=-1")]
    [InlineData("pageSize=101")]
    [InlineData("pageSize=2147483647")]
    [InlineData("page=2147483647&pageSize=2")]
    [InlineData("page=abc")]
    [InlineData("pageSize=abc")]
    [InlineData("isActive=invalid")]
    public async Task Products_InvalidPaginationOrBindingReturnsBadRequest(string query)
    {
        await AuthenticateAsync();
        using var response = await _client.GetAsync("/api/products?" + query);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("?pageSize=100")]
    [InlineData("?page=2147483647&pageSize=1")]
    public async Task Products_ValidPaginationBoundariesDoNotOverflow(string query)
    {
        await AuthenticateAsync();
        var result = await GetProductsAsync(query);
        Assert.True(result.TotalCount > 0);
        Assert.InRange(result.Items.Count, 0, result.PageSize);
        if (result.Page == int.MaxValue)
        {
            Assert.Empty(result.Items);
        }
    }

    [Fact]
    public async Task Customers_ReturnsOnlyActiveSelectionDtosAndFiltersInSql()
    {
        await AuthenticateAsync();
        await _factory.BeginTestTransactionAsync();
        try
        {
            await using var scope = _factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var prefix = "C3-" + Guid.NewGuid().ToString("N")[..8];
            var active = Customer(prefix + "-A", true);
            var inactive = Customer(prefix + "-I", false);
            db.Customers.AddRange(active, inactive);
            await db.SaveChangesAsync();

            _factory.ExecutedQueries.Clear();
            using var response = await _client.GetAsync("/api/customers");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var items = json.RootElement.EnumerateArray().ToArray();
            Assert.Contains(items, x => x.GetProperty("id").GetInt32() == active.Id);
            Assert.DoesNotContain(items, x => x.GetProperty("id").GetInt32() == inactive.Id);
            Assert.DoesNotContain(items, x => x.GetProperty("code").GetString() == "CUST-005");
            Assert.All(items, x => Assert.Equal(new[] { "code", "id", "name" },
                x.EnumerateObject().Select(property => property.Name).OrderBy(name => name)));
            var activeItem = Assert.Single(items, x => x.GetProperty("id").GetInt32() == active.Id);
            Assert.Equal(active.Code, activeItem.GetProperty("code").GetString());
            Assert.Equal(active.Name, activeItem.GetProperty("name").GetString());
            var codes = items.Select(x => x.GetProperty("code").GetString()).ToArray();
            Assert.Equal(codes.OrderBy(x => x, StringComparer.Ordinal), codes);

            var sql = Assert.Single(_factory.ExecutedQueries, x => x.Contains("FROM [Customers]"));
            Assert.Contains("WHERE", sql);
            Assert.Contains("[IsActive]", sql);
            Assert.Contains("ORDER BY", sql);
            Assert.DoesNotContain("[PhoneNumber]", sql);
            Assert.DoesNotContain("[CreatedAt]", sql);
        }
        finally
        {
            await _factory.RollbackTestTransactionAsync();
        }
    }

    [Theory]
    [InlineData("admin", "Admin123!")]
    [InlineData("sales", "Sales123!")]
    public async Task Customers_IsAccessibleToBothExistingRoles(string username, string password)
    {
        await AuthenticateAsync(username, password);
        using var response = await _client.GetAsync("/api/customers");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task AuthenticateAsync(string username = "sales", string password = "Sales123!")
    {
        using var response = await _client.PostAsJsonAsync("/api/auth/login", new { username, password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            json.RootElement.GetProperty("token").GetString());
    }

    private async Task<ProductListResponse> GetProductsAsync(string query)
    {
        using var response = await _client.GetAsync("/api/products" + query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ProductListResponse>())!;
    }

    private static Product Product(string code, bool active) => new()
    {
        Code = code,
        Name = "Step 3 product " + code,
        Price = 12.34m,
        StockQuantity = 7,
        IsActive = active,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    private static Customer Customer(string code, bool active) => new()
    {
        Code = code,
        Name = "Step 3 customer " + code,
        PhoneNumber = "09120000000",
        IsActive = active,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    public void Dispose() => _client.Dispose();
}
