using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Challenge.Api.Auth;
using Challenge.Api.Data;
using Challenge.Api.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Challenge.Api.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IPasswordHasher<User> _passwordHasher;
    private readonly JwtOptions _jwt;

    public AuthController(AppDbContext db, IPasswordHasher<User> passwordHasher, IOptions<JwtOptions> jwt)
    {
        _db = db;
        _passwordHasher = passwordHasher;
        _jwt = jwt.Value;
    }

    public record LoginRequest(
        [Required, StringLength(64)] string Username,
        [Required] string Password);

    public record LoginUserResponse(int Id, string Username, string Role);
    public record LoginResponse(string Token, DateTime ExpiresAt, LoginUserResponse User);

    [AllowAnonymous]
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request, CancellationToken cancellationToken)
    {
        var user = await _db.Users.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Username == request.Username, cancellationToken);

        if (user is null || !user.IsActive ||
            _passwordHasher.VerifyHashedPassword(user, user.PasswordHash, request.Password) ==
            PasswordVerificationResult.Failed)
        {
            return Unauthorized(new { message = "Invalid username or password." });
        }

        var now = DateTime.UtcNow;
        var expiresAt = now.AddMinutes(_jwt.ExpirationMinutes);
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString(CultureInfo.InvariantCulture)),
            new Claim(JwtRegisteredClaimNames.UniqueName, user.Username),
            new Claim("role", user.Role.ToString())
        };
        var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwt.Key));
        var token = new JwtSecurityToken(
            issuer: _jwt.Issuer,
            audience: _jwt.Audience,
            claims: claims,
            notBefore: now,
            expires: expiresAt,
            signingCredentials: new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256));

        return Ok(new LoginResponse(
            new JwtSecurityTokenHandler().WriteToken(token),
            expiresAt,
            new LoginUserResponse(user.Id, user.Username, user.Role.ToString())));
    }
}
