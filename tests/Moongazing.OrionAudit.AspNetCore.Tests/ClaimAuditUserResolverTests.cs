using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moongazing.OrionAudit;
using Moongazing.OrionAudit.AspNetCore;

namespace Moongazing.OrionAudit.AspNetCore.Tests;

public sealed class ClaimAuditUserResolverTests
{
    private static ServiceProvider BuildSpWithUser(ClaimsPrincipal? principal, IAuditUserEnricher? enricher = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor
        {
            HttpContext = principal is null
                ? new DefaultHttpContext()
                : new DefaultHttpContext { User = principal },
        });
        if (enricher is not null)
        {
            services.AddSingleton(enricher);
        }
        return services.BuildServiceProvider();
    }

    private static ClaimsPrincipal Auth(params (string Type, string Value)[] claims)
    {
        var identity = new ClaimsIdentity(authenticationType: "test");
        foreach (var (t, v) in claims)
        {
            identity.AddClaim(new Claim(t, v));
        }
        return new ClaimsPrincipal(identity);
    }

    private static ClaimAuditUserResolver NewResolver(Action<ClaimAuditUserResolverOptions>? configure = null)
    {
        var options = new ClaimAuditUserResolverOptions();
        configure?.Invoke(options);
        return new ClaimAuditUserResolver(Options.Create(options));
    }

    [Fact]
    public void Resolve_returns_null_when_no_http_context()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        var resolver = NewResolver();

        Assert.Null(resolver.Resolve(sp));
    }

    [Fact]
    public void Resolve_returns_null_when_anonymous_and_RequireAuthenticated_is_true()
    {
        var sp = BuildSpWithUser(new ClaimsPrincipal(new ClaimsIdentity()));
        var resolver = NewResolver();

        Assert.Null(resolver.Resolve(sp));
    }

    [Fact]
    public void Resolve_finds_first_matching_claim_in_order()
    {
        // sub is tried first; should win over NameIdentifier even though both are present.
        var principal = Auth(("sub", "sub-id"), (ClaimTypes.NameIdentifier, "name-id-id"));
        var sp = BuildSpWithUser(principal);
        var resolver = NewResolver();

        var resolved = resolver.Resolve(sp);

        Assert.NotNull(resolved);
        Assert.Equal("sub-id", resolved!.Id);
    }

    [Fact]
    public void Resolve_falls_back_to_oid_when_sub_and_NameIdentifier_missing()
    {
        // Azure AD often stamps `oid` for the object id without `sub` in legacy tokens.
        var principal = Auth(("oid", "azure-oid"));
        var sp = BuildSpWithUser(principal);
        var resolver = NewResolver();

        var resolved = resolver.Resolve(sp);

        Assert.Equal("azure-oid", resolved!.Id);
    }

    [Fact]
    public void Resolve_uses_DisplayNameClaimTypes_in_order()
    {
        var principal = Auth(
            ("sub", "u-1"),
            ("name", "shorthand-name"),
            (ClaimTypes.Name, "long-name"));
        var sp = BuildSpWithUser(principal);
        var resolver = NewResolver();

        var resolved = resolver.Resolve(sp);

        // Default order tries ClaimTypes.Name first.
        Assert.Equal("long-name", resolved!.DisplayName);
    }

    [Fact]
    public void Resolve_uses_TypeClaimType_when_configured()
    {
        var principal = Auth(("sub", "svc-1"), ("idp_kind", "service-principal"));
        var sp = BuildSpWithUser(principal);
        var resolver = NewResolver(o => o.TypeClaimType = "idp_kind");

        var resolved = resolver.Resolve(sp);

        Assert.Equal("service-principal", resolved!.Type);
    }

    [Fact]
    public void Resolve_uses_DefaultUserType_when_no_TypeClaim()
    {
        var principal = Auth(("sub", "u-1"));
        var sp = BuildSpWithUser(principal);
        var resolver = NewResolver(o => o.DefaultUserType = "interactive");

        var resolved = resolver.Resolve(sp);

        Assert.Equal("interactive", resolved!.Type);
    }

    [Fact]
    public void Resolve_scans_unauthenticated_principal_when_RequireAuthenticated_is_false()
    {
        var identity = new ClaimsIdentity(); // no authenticationType -> NOT authenticated
        identity.AddClaim(new Claim("sub", "u-1"));
        var sp = BuildSpWithUser(new ClaimsPrincipal(identity));
        var resolver = NewResolver(o => o.RequireAuthenticated = false);

        var resolved = resolver.Resolve(sp);

        Assert.Equal("u-1", resolved!.Id);
    }

    [Fact]
    public void Resolve_returns_null_when_no_configured_claim_matches()
    {
        var principal = Auth(("custom-claim", "x"));
        var sp = BuildSpWithUser(principal);
        var resolver = NewResolver();

        Assert.Null(resolver.Resolve(sp));
    }

    [Fact]
    public void Resolve_passes_through_enricher_when_registered()
    {
        var principal = Auth(("sub", "u-1"));
        var enricher = new RecordingEnricher(
            (user, _) => new AuditUser(user.Id, "Enriched Display", "ldap-user"));
        var sp = BuildSpWithUser(principal, enricher);
        var resolver = NewResolver();

        var resolved = resolver.Resolve(sp);

        Assert.Equal("u-1", resolved!.Id);
        Assert.Equal("Enriched Display", resolved.DisplayName);
        Assert.Equal("ldap-user", resolved.Type);
        Assert.Equal(1, enricher.Invocations);
    }

    [Fact]
    public void Resolve_returns_null_when_enricher_returns_null()
    {
        var principal = Auth(("sub", "u-1"));
        var enricher = new RecordingEnricher((_, _) => null);
        var sp = BuildSpWithUser(principal, enricher);
        var resolver = NewResolver();

        var resolved = resolver.Resolve(sp);

        Assert.Null(resolved);
        Assert.Equal(1, enricher.Invocations);
    }

    private sealed class RecordingEnricher : IAuditUserEnricher
    {
        private readonly Func<AuditUser, IServiceProvider, AuditUser?> impl;
        public int Invocations { get; private set; }

        public RecordingEnricher(Func<AuditUser, IServiceProvider, AuditUser?> impl) => this.impl = impl;

        public AuditUser? Enrich(AuditUser user, IServiceProvider serviceProvider)
        {
            Invocations++;
            return impl(user, serviceProvider);
        }
    }
}

public sealed class AddOrionAuditClaimResolverTests
{
    [Fact]
    public void AddOrionAuditClaimResolver_replaces_HttpContextAuditUserResolver()
    {
        var services = new ServiceCollection();
        services.AddOrionAuditAspNetCore();         // wires HttpContextAuditUserResolver
        services.AddOrionAuditClaimResolver();      // must REPLACE, not stack

        using var sp = services.BuildServiceProvider();
        var resolver = sp.GetRequiredService<IAuditUserResolver>();

        Assert.IsType<ClaimAuditUserResolver>(resolver);
    }

    [Fact]
    public void AddOrionAuditClaimResolver_applies_configure_callback()
    {
        var services = new ServiceCollection();
        services.AddOrionAuditClaimResolver(o => o.DefaultUserType = "system");

        using var sp = services.BuildServiceProvider();
        var options = sp.GetRequiredService<IOptions<ClaimAuditUserResolverOptions>>().Value;

        Assert.Equal("system", options.DefaultUserType);
    }
}
