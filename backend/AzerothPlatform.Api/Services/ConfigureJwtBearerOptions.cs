using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace AzerothPlatform.Api.Services;

/// <summary>
/// Configures the JWT bearer scheme using the <see cref="AdminAuthService"/> singleton so token
/// validation uses exactly the same signing key that issued the token.
/// </summary>
public sealed class ConfigureJwtBearerOptions : IConfigureNamedOptions<JwtBearerOptions>
{
    private readonly AdminAuthService _auth;

    public ConfigureJwtBearerOptions(AdminAuthService auth)
    {
        _auth = auth;
    }

    public void Configure(string? name, JwtBearerOptions options) => Configure(options);

    public void Configure(JwtBearerOptions options)
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = AdminAuthService.Issuer,
            ValidateAudience = true,
            ValidAudience = AdminAuthService.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = _auth.SigningKey,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1),
            // Pin the algorithm to HS256 so a token cannot be accepted under a weaker/other algorithm
            // (e.g. an "alg: none" or RS/HS confusion attack).
            ValidAlgorithms = new[] { SecurityAlgorithms.HmacSha256 },
        };

        // SignalR WebSockets can't send an Authorization header, so accept the token from the
        // access_token query string, but ONLY for hub connections (never for the REST API). This is the
        // Microsoft-recommended pattern. Security tradeoff: query strings can be captured by reverse
        // proxies / access logs. If exposing this behind a proxy, disable query-string logging for
        // /hubs/* and keep the admin token lifetime short (Auth:TokenLifetimeHours). An Authorization
        // header still takes precedence when present.
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                var path = context.HttpContext.Request.Path;
                var hasAuthHeader = context.Request.Headers.ContainsKey("Authorization");
                if (!hasAuthHeader && !string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs"))
                {
                    context.Token = accessToken;
                }
                return Task.CompletedTask;
            }
        };
    }
}
