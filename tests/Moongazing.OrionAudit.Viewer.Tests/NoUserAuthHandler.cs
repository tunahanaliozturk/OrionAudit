using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Moongazing.OrionAudit.Viewer.Tests;

/// <summary>
/// Authentication handler used by the viewer auth tests. Always returns <see cref="AuthenticateResult.NoResult"/>
/// so the request is treated as unauthenticated; with no authenticated user the
/// authorization middleware challenges via this scheme and produces a clean 401.
/// </summary>
internal sealed class NoUserAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public NoUserAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        => Task.FromResult(AuthenticateResult.NoResult());
}
