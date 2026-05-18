namespace OrionAudit.Testing;

/// <summary>
/// Test double for <see cref="IAuditTenantResolver"/>. Returns the configured tenant id regardless
/// of the supplied service provider.
/// </summary>
public sealed class InMemoryAuditTenantResolver : IAuditTenantResolver
{
    /// <summary>Initializes a new resolver returning <paramref name="tenantId"/> (default null).</summary>
    public InMemoryAuditTenantResolver(string? tenantId = null) => TenantId = tenantId;

    /// <summary>The tenant id returned on resolve. Mutable so tests can swap mid-run.</summary>
    public string? TenantId { get; set; }

    /// <inheritdoc />
    public string? Resolve(IServiceProvider serviceProvider) => TenantId;
}
