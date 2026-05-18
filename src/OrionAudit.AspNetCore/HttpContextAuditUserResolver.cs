using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace OrionAudit.AspNetCore;

/// <summary>
/// <see cref="IAuditUserResolver"/> implementation that pulls the current user from
/// <see cref="IHttpContextAccessor"/>. Returns null for anonymous requests or when
/// <see cref="IHttpContextAccessor.HttpContext"/> is not available.
/// </summary>
public sealed class HttpContextAuditUserResolver : IAuditUserResolver
{
    /// <inheritdoc />
    public AuditUser? Resolve(IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        var accessor = serviceProvider.GetService<IHttpContextAccessor>();
        var user = accessor?.HttpContext?.User;
        if (user?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        var id = user.FindFirst(ClaimTypes.NameIdentifier)?.Value
              ?? user.FindFirst("sub")?.Value;
        if (id is null)
        {
            return null;
        }

        var display = user.FindFirst(ClaimTypes.Name)?.Value;
        return new AuditUser(id, display, "user");
    }
}
