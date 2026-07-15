using AzerothPlatform.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AzerothPlatform.Api.Controllers;

/// <summary>
/// Single-admin authentication endpoints. Login is anonymous; everything else in the admin API
/// requires the issued bearer token.
/// </summary>
[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly AdminAuthService _auth;

    public AuthController(AdminAuthService auth)
    {
        _auth = auth;
    }

    public sealed record LoginRequest(string Password);
    public sealed record LoginResponse(string Token);

    [AllowAnonymous]
    [EnableRateLimiting("login")]
    [HttpPost("login")]
    public IActionResult Login([FromBody] LoginRequest request)
    {
        if (!_auth.ValidatePassword(request?.Password))
        {
            return Unauthorized(new { error = "Invalid password." });
        }

        return Ok(new LoginResponse(_auth.CreateToken()));
    }

    /// <summary>Validates the current token; used by the frontend to gate protected routes.</summary>
    [Authorize]
    [HttpGet("me")]
    public IActionResult Me() => Ok(new { authenticated = true, name = User.Identity?.Name ?? "admin" });

    /// <summary>Logout is client-side (drop the token); provided for symmetry.</summary>
    [AllowAnonymous]
    [HttpPost("logout")]
    public IActionResult Logout() => Ok();
}
