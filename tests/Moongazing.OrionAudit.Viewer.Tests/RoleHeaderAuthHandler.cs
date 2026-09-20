using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Moongazing.OrionAudit.Viewer.Tests;

/// <summary>
/// Authentication handler used by the viewer auth tests. A request carrying the
/// <see cref="RolesHeader"/> header is authenticated with the roles it names; a request without it
/// yields <see cref="AuthenticateResult.NoResult"/> so the request stays unauthenticated and the
/// authorization middleware challenges via this scheme and produces a clean 401. One scheme can
/// therefore drive all three cases the viewer cares about: anonymous, authenticated-but-outside
/// the policy, and authenticated-and-inside it.
/// </summary>
internal sealed class RoleHeaderAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    /// <summary>Request header naming the comma-separated roles to authenticate the caller with.</summary>
    public const string RolesHeader = "X-Test-Roles";

    public RoleHeaderAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(RolesHeader, out var roles))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new List<Claim> { new(ClaimTypes.Name, "test-user") };
        claims.AddRange(roles.ToString()
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(role => new Claim(ClaimTypes.Role, role)));

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(principal, Scheme.Name)));
    }
}
