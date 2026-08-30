using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AzerothPlatform.Tests.Support;

/// <summary>
/// Test authentication handler used by the in-process test host. It authenticates every request as an
/// admin so the functional smoke tests keep working under the deny-by-default authorization policy,
/// EXCEPT requests that carry the <c>X-Anon</c> header, which are left unauthenticated so tests can
/// assert that protected routes correctly return 401.
/// </summary>
public sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "Test";
    public const string AnonymousHeader = "X-Anon";

    public TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Opt-out header → behave as an anonymous caller (no ticket), so [Authorize]/FallbackPolicy 401s.
        if (Request.Headers.ContainsKey(AnonymousHeader))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new[]
        {
            new Claim(ClaimTypes.Name, "test-admin"),
            new Claim(ClaimTypes.Role, "Admin")
        };
        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
