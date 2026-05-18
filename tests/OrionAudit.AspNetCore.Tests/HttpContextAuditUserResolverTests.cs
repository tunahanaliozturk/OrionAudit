using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OrionAudit;
using OrionAudit.AspNetCore;

namespace OrionAudit.AspNetCore.Tests;

public class HttpContextAuditUserResolverTests
{
    private static ServiceProvider BuildSpWithUser(ClaimsPrincipal? principal)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor
        {
            HttpContext = principal is null
                ? new DefaultHttpContext()
                : new DefaultHttpContext { User = principal }
        });
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Resolve_ReturnsNull_WhenNoHttpContext()
    {
        var services = new ServiceCollection();
        var sp = services.BuildServiceProvider();
        var resolver = new HttpContextAuditUserResolver();
        Assert.Null(resolver.Resolve(sp));
    }

    [Fact]
    public void Resolve_ReturnsNull_WhenUserNotAuthenticated()
    {
        var sp = BuildSpWithUser(new ClaimsPrincipal(new ClaimsIdentity()));
        var resolver = new HttpContextAuditUserResolver();
        Assert.Null(resolver.Resolve(sp));
    }

    [Fact]
    public void Resolve_ReturnsAuditUser_WhenNameIdentifierClaimPresent()
    {
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "user-123"),
            new Claim(ClaimTypes.Name, "Alice")
        }, authenticationType: "Test");
        var sp = BuildSpWithUser(new ClaimsPrincipal(identity));

        var resolver = new HttpContextAuditUserResolver();
        var user = resolver.Resolve(sp);
        Assert.NotNull(user);
        Assert.Equal("user-123", user.Id);
        Assert.Equal("Alice", user.DisplayName);
        Assert.Equal("user", user.Type);
    }

    [Fact]
    public void Resolve_FallsBackToSubClaim_WhenNameIdentifierAbsent()
    {
        var identity = new ClaimsIdentity(new[] { new Claim("sub", "user-456") }, authenticationType: "Test");
        var sp = BuildSpWithUser(new ClaimsPrincipal(identity));

        var resolver = new HttpContextAuditUserResolver();
        var user = resolver.Resolve(sp);
        Assert.NotNull(user);
        Assert.Equal("user-456", user.Id);
    }
}
