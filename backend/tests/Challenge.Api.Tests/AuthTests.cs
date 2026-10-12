using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Challenge.Api.Auth;
using Challenge.Api.Data;
using Challenge.Api.Domain;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Challenge.Api.Tests;

public class AuthTests : IClassFixture<AuthApiFactory>, IDisposable
{
    private readonly AuthApiFactory _factory;
    private readonly HttpClient _client;

    public AuthTests(AuthApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Theory]
    [InlineData("admin", "Admin123!", "Admin")]
    [InlineData("sales", "Sales123!", "Sales")]
    public async Task Login_WithSeededCredentials_ReturnsSignedJwtAndSafeUserData(
        string username, string password, string role)
    {
        using var response = await _client.PostAsJsonAsync("/api/auth/login", new { username, password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        Assert.Equal(new[] { "expiresAt", "token", "user" },
            root.EnumerateObject().Select(x => x.Name).OrderBy(x => x));
        var user = root.GetProperty("user");
        Assert.Equal(new[] { "id", "role", "username" },
            user.EnumerateObject().Select(x => x.Name).OrderBy(x => x));
        Assert.Equal(username, user.GetProperty("username").GetString());
        Assert.Equal(role, user.GetProperty("role").GetString());

        var validation = _factory.Services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme).TokenValidationParameters.Clone();
        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        var principal = handler.ValidateToken(root.GetProperty("token").GetString()!, validation,
            out var securityToken);
        var jwt = Assert.IsType<JwtSecurityToken>(securityToken);
        Assert.True(principal.Identity!.IsAuthenticated);
        Assert.Equal(username, principal.Identity.Name);
        Assert.Equal(user.GetProperty("id").GetInt32().ToString(),
            principal.FindFirst(JwtRegisteredClaimNames.Sub)!.Value);
        Assert.True(principal.IsInRole(role));
        Assert.Equal(SecurityAlgorithms.HmacSha256, jwt.Header.Alg);

        var options = _factory.Services.GetRequiredService<IOptions<JwtOptions>>().Value;
        Assert.Equal(options.Issuer, jwt.Issuer);
        Assert.Equal(new[] { options.Audience }, jwt.Audiences);
        var expiresAt = root.GetProperty("expiresAt").GetDateTime();
        Assert.Equal(DateTimeKind.Utc, expiresAt.Kind);
        Assert.InRange(jwt.ValidTo, expiresAt.AddSeconds(-1), expiresAt);
        Assert.InRange(expiresAt - jwt.ValidFrom,
            TimeSpan.FromMinutes(options.ExpirationMinutes),
            TimeSpan.FromMinutes(options.ExpirationMinutes).Add(TimeSpan.FromSeconds(1)));
    }

    [Theory]
    [InlineData("admin", "wrong-password")]
    [InlineData("sales", "wrong-password")]
    [InlineData("unknown-auth-user", "Admin123!")]
    public async Task Login_WithInvalidCredentials_ReturnsGenericUnauthorized(
        string username, string password)
    {
        using var response = await _client.PostAsJsonAsync("/api/auth/login", new { username, password });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("{\"message\":\"Invalid username or password.\"}",
            await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(null, "Admin123!")]
    [InlineData("", "Admin123!")]
    [InlineData("   ", "Admin123!")]
    [InlineData("admin", null)]
    [InlineData("admin", "")]
    [InlineData("admin", "   ")]
    public async Task Login_WithMissingCredentials_ReturnsBadRequest(string? username, string? password)
    {
        using var response = await _client.PostAsJsonAsync("/api/auth/login", new { username, password });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Login_WithOverlongUsername_ReturnsBadRequest()
    {
        using var response = await _client.PostAsJsonAsync("/api/auth/login",
            new { username = new string('a', 65), password = "Admin123!" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Login_WithInactiveUser_ReturnsUnauthorizedAndRollsBackTestData()
    {
        await _factory.BeginTestTransactionAsync();
        try
        {
            await using var scope = _factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>();
            var user = new User
            {
                Username = "auth-test-" + Guid.NewGuid().ToString("N"),
                IsActive = false,
                Role = UserRole.Sales,
                CreatedAt = DateTime.UtcNow
            };
            const string password = "Inactive-test123!";
            user.PasswordHash = hasher.HashPassword(user, password);
            db.Users.Add(user);
            await db.SaveChangesAsync();

            using var response = await _client.PostAsJsonAsync("/api/auth/login",
                new { username = user.Username, password });
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("{\"message\":\"Invalid username or password.\"}",
                await response.Content.ReadAsStringAsync());
        }
        finally
        {
            await _factory.RollbackTestTransactionAsync();
        }
    }

    [Theory]
    [InlineData("GET", "/api/products")]
    [InlineData("GET", "/api/customers")]
    [InlineData("GET", "/api/orders")]
    [InlineData("GET", "/api/orders/1")]
    [InlineData("POST", "/api/orders")]
    [InlineData("GET", "/api/reports/top-products")]
    public async Task BusinessEndpoint_WithoutToken_ReturnsUnauthorized(string method, string path)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("admin", "Admin123!")]
    [InlineData("sales", "Sales123!")]
    public async Task BusinessEndpoint_WithSeededUserToken_ReachesExistingAction(
        string username, string password)
    {
        using var login = await _client.PostAsJsonAsync("/api/auth/login", new { username, password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var json = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/products");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer",
            json.RootElement.GetProperty("token").GetString());
        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var products = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Array, products.RootElement.GetProperty("items").ValueKind);
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("wrong-issuer")]
    [InlineData("wrong-audience")]
    [InlineData("wrong-signature")]
    [InlineData("missing-expiration")]
    [InlineData("unsigned")]
    public async Task BusinessEndpoint_WithInvalidJwt_ReturnsUnauthorized(string variant)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/products");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken(variant, "Admin"));
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain("error_description", response.Headers.WwwAuthenticate.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Unknown")]
    public async Task BusinessEndpoint_WithoutPermittedRole_ReturnsForbidden(string? role)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/products");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken("valid", role));
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Health_WithoutToken_RemainsAccessible()
    {
        using var response = await _client.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private string CreateToken(string variant, string? role)
    {
        var options = _factory.Services.GetRequiredService<IOptions<JwtOptions>>().Value;
        var now = DateTime.UtcNow;
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, "1"),
            new(JwtRegisteredClaimNames.UniqueName, "admin")
        };
        if (role is not null)
        {
            claims.Add(new Claim("role", role));
        }

        var key = variant == "wrong-signature"
            ? RandomNumberGenerator.GetBytes(32)
            : Encoding.UTF8.GetBytes(options.Key);
        var token = new JwtSecurityToken(
            issuer: variant == "wrong-issuer" ? "invalid-issuer" : options.Issuer,
            audience: variant == "wrong-audience" ? "invalid-audience" : options.Audience,
            claims: claims,
            notBefore: now.AddMinutes(-2),
            expires: variant == "missing-expiration" ? null :
                variant == "expired" ? now.AddMinutes(-1) : now.AddMinutes(5),
            signingCredentials: variant == "unsigned" ? null :
                new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public void Dispose() => _client.Dispose();
}
