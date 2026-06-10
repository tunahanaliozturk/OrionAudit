namespace Moongazing.OrionAudit;

using System.Security.Claims;

/// <summary>
/// Configuration for the claim-driven audit user resolver
/// (<c>Moongazing.OrionAudit.AspNetCore.ClaimAuditUserResolver</c>).
/// </summary>
public sealed class ClaimAuditUserResolverOptions
{
    /// <summary>
    /// Ordered list of claim types tried in turn when extracting the user
    /// <see cref="AuditUser.Id"/>. The first claim that exists and is non-empty wins.
    /// Default: <c>sub</c>, <see cref="ClaimTypes.NameIdentifier"/>, <c>oid</c>,
    /// <c>preferred_username</c>. The defaults cover OpenID Connect (<c>sub</c>),
    /// classic ASP.NET Core (<see cref="ClaimTypes.NameIdentifier"/>), Azure AD object id
    /// (<c>oid</c>), and the upn fallback that some Microsoft tenants stamp instead of
    /// <c>sub</c>.
    /// </summary>
    public IList<string> IdClaimTypes { get; } = new List<string>
    {
        "sub",
        ClaimTypes.NameIdentifier,
        "oid",
        "preferred_username",
    };

    /// <summary>
    /// Ordered list of claim types tried in turn for <see cref="AuditUser.DisplayName"/>.
    /// Default: <see cref="ClaimTypes.Name"/>, <c>name</c>, <c>preferred_username</c>,
    /// <see cref="ClaimTypes.Email"/>, <c>email</c>. The first non-empty match wins;
    /// <see langword="null"/> when none match.
    /// </summary>
    public IList<string> DisplayNameClaimTypes { get; } = new List<string>
    {
        ClaimTypes.Name,
        "name",
        "preferred_username",
        ClaimTypes.Email,
        "email",
    };

    /// <summary>
    /// Optional claim type whose value becomes <see cref="AuditUser.Type"/>. When unset
    /// (default), <see cref="AuditUser.Type"/> uses <see cref="DefaultUserType"/>. Useful
    /// for differentiating service principals from interactive users via a custom claim.
    /// </summary>
    public string? TypeClaimType { get; set; }

    /// <summary>
    /// Default value for <see cref="AuditUser.Type"/> when <see cref="TypeClaimType"/> is
    /// not set or the claim is missing. Default <c>"user"</c>.
    /// </summary>
    public string DefaultUserType { get; set; } = "user";

    /// <summary>
    /// When <see langword="true"/>, the resolver only attributes events from
    /// authenticated principals. When <see langword="false"/>, claims from an anonymous
    /// ClaimsPrincipal are still scanned. Default <see langword="true"/>; production
    /// deployments should keep this on.
    /// </summary>
    public bool RequireAuthenticated { get; set; } = true;
}
